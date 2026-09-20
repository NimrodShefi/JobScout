using Cronos;

namespace JobScout.Tests;

/// <summary>The cron expressions shipped in appsettings must parse, and they must fire at
/// the stated wall-clock time in the configured zone - including across a DST boundary,
/// which is where a UTC-only scheduler would quietly drift by an hour.</summary>
public class CronScheduleTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static CronExpression Parse(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        Assert.True(CronExpression.TryParse(cron, format, out var expression),
            $"'{cron}' should be a valid cron expression");

        return expression!;
    }

    [Theory]
    [InlineData("0 0 7 * * ?")]   // morning scan
    [InlineData("0 0 19 * * ?")]  // email check
    [InlineData("0 30 19 * * ?")] // discovery
    public void The_shipped_defaults_parse(string cron) => Parse(cron);

    [Fact]
    public void Seven_am_in_london_is_six_am_utc_during_british_summer_time()
    {
        var summer = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var next = Parse("0 0 7 * * ?").GetNextOccurrence(summer, London);

        Assert.NotNull(next);
        Assert.Equal(6, next!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void Seven_am_in_london_is_seven_am_utc_in_winter()
    {
        var winter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = Parse("0 0 7 * * ?").GetNextOccurrence(winter, London);

        Assert.NotNull(next);
        Assert.Equal(7, next!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void The_local_hour_stays_put_across_the_clock_change()
    {
        var expression = Parse("0 0 7 * * ?");

        // The British clocks go forward on the last Sunday in March.
        var before = new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero);

        var occurrences = expression
            .GetOccurrences(before, before.AddDays(5), London)
            .Select(o => TimeZoneInfo.ConvertTime(o, London).Hour)
            .ToList();

        Assert.NotEmpty(occurrences);
        Assert.All(occurrences, hour => Assert.Equal(7, hour));
    }

    [Fact]
    public void A_five_field_expression_is_read_as_standard_cron()
    {
        var next = Parse("0 7 * * *").GetNextOccurrence(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), London);

        Assert.Equal(6, next!.Value.UtcDateTime.Hour);
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("0 0 99 * * ?")]
    [InlineData("")]
    public void A_bad_expression_is_rejected_rather_than_guessed_at(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        Assert.False(CronExpression.TryParse(cron, format, out _));
    }
}

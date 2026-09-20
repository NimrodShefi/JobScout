using System.Globalization;
using JobScout.Core.Entities;

namespace JobScout.Web.Services;

/// <summary>Formatting shared by the pages. Times are stored in UTC and shown in my
/// configured time zone so the dashboard matches the clock on the wall.</summary>
public sealed class Display(JobScheduler scheduler)
{
    private TimeZoneInfo Zone => scheduler.TimeZone;

    public string DateTimeLocal(DateTimeOffset? value, string format = "dd MMM yyyy HH:mm") =>
        value is null ? "-" : TimeZoneInfo.ConvertTime(value.Value, Zone).ToString(format, CultureInfo.CurrentCulture);

    public string DateLocal(DateTimeOffset? value) => DateTimeLocal(value, "dd MMM yyyy");

    /// <summary>"3 days ago", "in 2 hours". Easier to scan than a timestamp on a dashboard.</summary>
    public static string Relative(DateTimeOffset? value)
    {
        if (value is null) return "-";

        var delta = value.Value - DateTimeOffset.UtcNow;
        var future = delta > TimeSpan.Zero;
        var span = future ? delta : -delta;

        var text = span switch
        {
            { TotalSeconds: < 60 } => "moments",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min",
            { TotalHours: < 24 } => Plural((int)span.TotalHours, "hour"),
            { TotalDays: < 31 } => Plural((int)span.TotalDays, "day"),
            _ => Plural((int)(span.TotalDays / 30), "month"),
        };

        return future ? $"in {text}" : $"{text} ago";
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";

    public static string Duration(TimeSpan? span) => span switch
    {
        null => "-",
        { TotalSeconds: < 1 } => "<1s",
        { TotalMinutes: < 1 } => $"{span.Value.TotalSeconds:0}s",
        { TotalHours: < 1 } => $"{span.Value.Minutes}m {span.Value.Seconds}s",
        _ => $"{(int)span.Value.TotalHours}h {span.Value.Minutes}m",
    };

    /// <summary>Salary range, or an explicit "not stated" - an unknown salary is a fact
    /// worth showing, not a blank.</summary>
    public static string Salary(JobListing listing)
    {
        if (!listing.SalaryKnown) return "not stated";

        var symbol = CurrencySymbol(listing.SalaryCurrency);

        return (listing.SalaryMin, listing.SalaryMax) switch
        {
            (null, null) => "not stated",
            ({ } min, null) => $"{symbol}{min:N0}+",
            (null, { } max) => $"up to {symbol}{max:N0}",
            ({ } min, { } max) when min == max => $"{symbol}{min:N0}",
            ({ } min, { } max) => $"{symbol}{min:N0} - {symbol}{max:N0}",
        };
    }

    public static string CurrencySymbol(string? code) => code?.ToUpperInvariant() switch
    {
        "GBP" => "£",
        "USD" => "$",
        "EUR" => "€",
        null or "" => "",
        var other => other + " ",
    };

    /// <summary>Splits the newline-joined strengths/gaps back into a list for display.</summary>
    public static IReadOnlyList<string> Lines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max].TrimEnd() + "...";

    /// <summary>Host of a URL, for a compact link label.</summary>
    public static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url ?? string.Empty;
}

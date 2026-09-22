using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>The no-response rule: an application that hears nothing for long enough is marked
/// NoResponse, and a late reply can still move it on.</summary>
public class NoResponseRuleTests : IDisposable
{
    private const int Days = 21;

    private readonly TestDatabase _db = new();
    private readonly ApplicationService _service;
    private readonly DateTimeOffset _now = new(2026, 9, 22, 19, 0, 0, TimeSpan.Zero);

    public NoResponseRuleTests() =>
        _service = new ApplicationService(_db, NullLogger<ApplicationService>.Instance);

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedAsync(int daysAgo, ApplicationStatus status = ApplicationStatus.Applied)
    {
        var application = await _service.CreateManualAsync("Acme", "Engineer", _now.AddDays(-daysAgo), null);

        if (status != ApplicationStatus.Applied)
        {
            await _service.ChangeStatusAsync(application.Id, status);

            // Backdate the change so only the application date decides staleness.
            await using var db = _db.CreateDbContext();
            foreach (var h in db.ApplicationStatusChanges.Where(h => h.JobApplicationId == application.Id))
                h.ChangedAt = _now.AddDays(-daysAgo);
            await db.SaveChangesAsync();
        }

        return application.Id;
    }

    private async Task<ApplicationStatus> StatusOf(int id)
    {
        await using var db = _db.CreateDbContext();
        return (await db.Applications.SingleAsync(a => a.Id == id)).Status;
    }

    // ---- the pure rule --------------------------------------------------

    [Theory]
    [InlineData(ApplicationStatus.Applied, 21, true)]
    [InlineData(ApplicationStatus.Applied, 20, false)]
    [InlineData(ApplicationStatus.Acknowledged, 30, true)]
    [InlineData(ApplicationStatus.Interview, 60, false)]
    [InlineData(ApplicationStatus.Offer, 60, false)]
    [InlineData(ApplicationStatus.Rejected, 60, false)]
    [InlineData(ApplicationStatus.NoResponse, 60, false)]
    public void Only_applications_awaiting_a_reply_go_stale(ApplicationStatus status, int daysAgo, bool expected) =>
        Assert.Equal(expected, ApplicationStatusRules.IsStale(status, _now.AddDays(-daysAgo), _now, Days));

    [Fact]
    public void Zero_days_turns_the_rule_off() =>
        Assert.False(ApplicationStatusRules.IsStale(ApplicationStatus.Applied, _now.AddYears(-1), _now, 0));

    // ---- the service ----------------------------------------------------

    [Fact]
    public async Task A_silent_application_is_marked_no_response_with_a_history_row()
    {
        var id = await SeedAsync(daysAgo: 21);

        var moved = await _service.MarkStaleAsync(Days, _now);

        Assert.Equal(1, moved);
        Assert.Equal(ApplicationStatus.NoResponse, await StatusOf(id));

        await using var db = _db.CreateDbContext();
        var latest = await db.ApplicationStatusChanges
            .Where(h => h.JobApplicationId == id)
            .OrderByDescending(h => h.Id)
            .FirstAsync();

        Assert.Equal(ApplicationStatus.Applied, latest.FromStatus);
        Assert.Equal(ApplicationStatus.NoResponse, latest.ToStatus);
        Assert.Equal(StatusChangeSource.AgeRule, latest.Source);
    }

    [Fact]
    public async Task A_recent_application_is_left_alone()
    {
        var id = await SeedAsync(daysAgo: 20);

        Assert.Equal(0, await _service.MarkStaleAsync(Days, _now));
        Assert.Equal(ApplicationStatus.Applied, await StatusOf(id));
    }

    [Fact]
    public async Task A_recent_email_resets_the_clock()
    {
        var id = await SeedAsync(daysAgo: 40);

        await using (var db = _db.CreateDbContext())
        {
            var a = await db.Applications.SingleAsync(x => x.Id == id);
            a.LastEmailAt = _now.AddDays(-5);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await _service.MarkStaleAsync(Days, _now));
        Assert.Equal(ApplicationStatus.Applied, await StatusOf(id));
    }

    [Fact]
    public async Task A_recent_status_change_resets_the_clock()
    {
        var id = await SeedAsync(daysAgo: 40);

        // Acknowledged "now" - the change itself is fresh activity.
        await _service.ChangeStatusAsync(id, ApplicationStatus.Acknowledged);

        Assert.Equal(0, await _service.MarkStaleAsync(Days, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task An_acknowledged_application_still_goes_stale()
    {
        var id = await SeedAsync(daysAgo: 30, ApplicationStatus.Acknowledged);

        Assert.Equal(1, await _service.MarkStaleAsync(Days, _now));
        Assert.Equal(ApplicationStatus.NoResponse, await StatusOf(id));
    }

    [Theory]
    [InlineData(ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Offer)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Engaged_or_finished_applications_are_never_touched(ApplicationStatus status)
    {
        var id = await SeedAsync(daysAgo: 60, status);

        Assert.Equal(0, await _service.MarkStaleAsync(Days, _now));
        Assert.Equal(status, await StatusOf(id));
    }

    [Fact]
    public async Task Zero_days_moves_nothing()
    {
        await SeedAsync(daysAgo: 400);
        Assert.Equal(0, await _service.MarkStaleAsync(0, _now));
    }

    [Fact]
    public async Task Running_twice_moves_an_application_once()
    {
        await SeedAsync(daysAgo: 30);

        Assert.Equal(1, await _service.MarkStaleAsync(Days, _now));
        Assert.Equal(0, await _service.MarkStaleAsync(Days, _now));
    }

    // ---- a late reply still lands ---------------------------------------

    [Theory]
    [InlineData(EmailClassificationKind.Rejection, ApplicationStatus.Rejected)]
    [InlineData(EmailClassificationKind.Interview, ApplicationStatus.Interview)]
    public async Task A_late_reply_moves_a_no_response_application(
        EmailClassificationKind kind, ApplicationStatus expected)
    {
        var id = await SeedAsync(daysAgo: 30);
        await _service.MarkStaleAsync(Days, _now);

        var email = new FakeEmailProvider();
        email.Messages.Add(new EmailMessage
        {
            MessageId = "late-1",
            Uid = 1,
            FromAddress = "recruiter@acme.test",
            Subject = "Your application",
            ReceivedAt = DateTimeOffset.UtcNow,
            BodySnippet = "Following up on your application.",
        });

        IReadOnlyList<(int Id, string Company, string Title)>? offered = null;
        var matcher = new FakeJobMatcher
        {
            ClassifyHandler = (open, _, _, _) =>
            {
                offered = open;
                return new EmailMatchResult
                {
                    ApplicationId = id,
                    Classification = kind,
                    Confidence = 0.95,
                    Rationale = "Late reply",
                };
            },
        };

        var processing = new EmailProcessingService(
            _db, email, matcher,
            Options.Create(new JobScoutOptions { Email = new EmailOptions { Provider = EmailProviderKind.Imap } }),
            NullLogger<EmailProcessingService>.Instance);

        await processing.ProcessAsync();

        Assert.NotNull(offered);
        Assert.Contains(offered!, o => o.Id == id);
        Assert.Equal(expected, await StatusOf(id));
    }
}

using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>How the email check turns a classification into a status change, a suggestion,
/// or nothing at all - and that it never stores an email body.</summary>
public class EmailProcessingTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeEmailProvider _email = new();
    private readonly FakeJobMatcher _matcher = new();
    private readonly ApplicationService _applications;

    private const double Threshold = 0.8;

    public EmailProcessingTests() =>
        _applications = new ApplicationService(_db, NullLogger<ApplicationService>.Instance);

    public void Dispose() => _db.Dispose();

    private EmailProcessingService BuildService(double autoApplyConfidence = Threshold)
    {
        var options = Options.Create(new JobScoutOptions
        {
            Email = new EmailOptions
            {
                Provider = EmailProviderKind.Imap,
                AutoApplyConfidence = autoApplyConfidence,
                BodySnippetChars = 1500,
            },
        });

        return new EmailProcessingService(
            _db, _email, _matcher, options, NullLogger<EmailProcessingService>.Instance);
    }

    private static EmailMessage Message(uint uid = 1, string subject = "About your application") => new()
    {
        MessageId = $"msg-{uid}",
        Uid = uid,
        FromAddress = "recruiter@acme.test",
        Subject = subject,
        ReceivedAt = DateTimeOffset.UtcNow,
        BodySnippet = "We would like to invite you to an interview.",
    };

    private async Task<int> SeedApplicationAsync(
        ApplicationStatus status = ApplicationStatus.Applied)
    {
        var application = await _applications.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow.AddDays(-3), null);

        if (status != ApplicationStatus.Applied)
            await _applications.ChangeStatusAsync(application.Id, status);

        return application.Id;
    }

    // ---- happy paths ----------------------------------------------------

    [Fact]
    public async Task A_confident_interview_email_moves_the_application_forward()
    {
        var applicationId = await SeedApplicationAsync();

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Interview,
            Confidence = 0.95,
            Rationale = "Invites you to interview",
        };

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(1, outcome.StatusesChanged);
        Assert.Equal(0, outcome.SuggestionsRaised);

        await using var db = _db.CreateDbContext();
        var application = await db.Applications.FindAsync(applicationId);

        Assert.Equal(ApplicationStatus.Interview, application!.Status);
        Assert.NotNull(application.LastEmailAt);

        var latest = await db.ApplicationStatusChanges
            .Where(h => h.JobApplicationId == applicationId)
            .OrderByDescending(h => h.Id).FirstAsync();

        Assert.Equal(StatusChangeSource.EmailAutomatic, latest.Source);
    }

    [Fact]
    public async Task A_low_confidence_match_becomes_a_suggestion_and_changes_nothing()
    {
        var applicationId = await SeedApplicationAsync();

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Interview,
            Confidence = 0.55,
            Rationale = "Might be about the interview",
        };

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(0, outcome.StatusesChanged);
        Assert.Equal(1, outcome.SuggestionsRaised);

        await using var db = _db.CreateDbContext();

        Assert.Equal(ApplicationStatus.Applied,
            (await db.Applications.FindAsync(applicationId))!.Status);

        var suggestion = await db.SuggestedStatusUpdates.SingleAsync();
        Assert.Equal(ApplicationStatus.Interview, suggestion.SuggestedStatus);
        Assert.False(suggestion.IsResolved);
        Assert.Equal(0.55, suggestion.Confidence, 3);
    }

    [Fact]
    public async Task Confidence_exactly_at_the_threshold_is_applied()
    {
        var applicationId = await SeedApplicationAsync();

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Acknowledged,
            Confidence = Threshold,
        };

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(1, outcome.StatusesChanged);
    }

    [Fact]
    public async Task An_unrelated_email_is_counted_and_otherwise_ignored()
    {
        var applicationId = await SeedApplicationAsync();

        _email.Messages.Add(Message(subject: "Weekly job alert"));
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = null,
            Classification = EmailClassificationKind.Unrelated,
            Confidence = 0.99,
        };

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(1, outcome.Unrelated);
        Assert.Equal(0, outcome.Matched);

        await using var db = _db.CreateDbContext();
        Assert.Equal(ApplicationStatus.Applied,
            (await db.Applications.FindAsync(applicationId))!.Status);
        Assert.Equal(0, await db.SuggestedStatusUpdates.CountAsync());
    }

    // ---- the awkward cases ----------------------------------------------

    [Fact]
    public async Task A_confident_email_that_would_move_backwards_becomes_a_suggestion_instead()
    {
        // A late "we received your application" after the interview is already booked.
        var applicationId = await SeedApplicationAsync(ApplicationStatus.Interview);

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Acknowledged,
            Confidence = 0.99,
        };

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(0, outcome.StatusesChanged);
        Assert.Equal(1, outcome.SuggestionsRaised);

        await using var db = _db.CreateDbContext();
        Assert.Equal(ApplicationStatus.Interview,
            (await db.Applications.FindAsync(applicationId))!.Status);
    }

    [Fact]
    public async Task A_rejection_lands_from_any_open_state()
    {
        var applicationId = await SeedApplicationAsync(ApplicationStatus.Interview);

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Rejection,
            Confidence = 0.9,
        };

        await BuildService().ProcessAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(ApplicationStatus.Rejected,
            (await db.Applications.FindAsync(applicationId))!.Status);
    }

    [Fact]
    public async Task The_same_suggestion_twice_does_not_stack_up()
    {
        var applicationId = await SeedApplicationAsync();

        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Interview,
            Confidence = 0.5,
        };

        _email.Messages.Add(Message(uid: 1));
        await BuildService().ProcessAsync();

        _email.Messages.Clear();
        _email.Messages.Add(Message(uid: 2));
        await BuildService().ProcessAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.SuggestedStatusUpdates.CountAsync(s => !s.IsResolved));
    }

    [Fact]
    public async Task A_matcher_that_gives_up_is_counted_as_a_failure_not_a_change()
    {
        await SeedApplicationAsync();

        _email.Messages.Add(Message());
        _matcher.ClassifyHandler = (_, _, _, _) => null;

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Equal(0, outcome.StatusesChanged);
        Assert.Equal(0, outcome.SuggestionsRaised);
    }

    [Fact]
    public async Task With_no_open_applications_the_matcher_is_not_called_at_all()
    {
        await SeedApplicationAsync(ApplicationStatus.Rejected);

        _email.Messages.Add(Message());

        var outcome = await BuildService().ProcessAsync();

        Assert.Equal(0, _matcher.ClassifyCalls);
        Assert.Equal(1, outcome.Unrelated);
    }

    [Fact]
    public async Task An_unconfigured_mailbox_is_reported_rather_than_failing()
    {
        _email.IsConfigured = false;

        var outcome = await BuildService().ProcessAsync();

        Assert.True(outcome.NotConfigured);
        Assert.Equal(0, outcome.MessagesRead);
    }

    // ---- privacy and checkpointing --------------------------------------

    [Fact]
    public async Task No_email_body_is_ever_written_to_the_database()
    {
        var applicationId = await SeedApplicationAsync();

        const string body = "SECRET-BODY-TEXT that must never be persisted anywhere.";

        _email.Messages.Add(Message() with { BodySnippet = body });
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            ApplicationId = applicationId,
            Classification = EmailClassificationKind.Interview,
            Confidence = 0.4,
            Rationale = "Looks like an interview invitation",
        };

        await BuildService().ProcessAsync();

        await using var db = _db.CreateDbContext();

        var suggestion = await db.SuggestedStatusUpdates.SingleAsync();
        Assert.DoesNotContain("SECRET-BODY-TEXT", suggestion.Rationale ?? string.Empty);
        Assert.DoesNotContain("SECRET-BODY-TEXT", suggestion.EmailSubject ?? string.Empty);

        // Sender and subject are kept so I can recognise the message; the body is not.
        Assert.Equal("recruiter@acme.test", suggestion.EmailFrom);

        var history = await db.ApplicationStatusChanges.ToListAsync();
        Assert.All(history, h => Assert.DoesNotContain("SECRET-BODY-TEXT", h.Note ?? string.Empty));
    }

    [Fact]
    public async Task The_checkpoint_advances_so_the_next_run_only_reads_new_mail()
    {
        await SeedApplicationAsync();

        _email.Messages.Add(Message(uid: 7));
        _matcher.ClassifyHandler = (_, _, _, _) => new EmailMatchResult
        {
            Classification = EmailClassificationKind.Unrelated,
        };

        await BuildService().ProcessAsync();

        await using var db = _db.CreateDbContext();
        var checkpoint = await db.EmailCheckpoints.SingleAsync();

        Assert.Equal("fake:inbox", checkpoint.MailboxKey);
        Assert.Equal(7u, checkpoint.LastUid);
        Assert.NotNull(checkpoint.LastCheckedAt);

        // The second run is handed that cursor.
        _email.Messages.Clear();
        await BuildService().ProcessAsync();

        Assert.Equal(7u, _email.LastCursor!.LastUid);
    }

    [Fact]
    public async Task The_checkpoint_never_moves_backwards()
    {
        await SeedApplicationAsync();

        _email.Messages.Add(Message(uid: 20));
        await BuildService().ProcessAsync();

        _email.Messages.Clear();
        _email.Messages.Add(Message(uid: 5));
        await BuildService().ProcessAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(20u, (await db.EmailCheckpoints.SingleAsync()).LastUid);
    }
}

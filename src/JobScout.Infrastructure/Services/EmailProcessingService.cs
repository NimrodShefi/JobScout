using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Services;

public sealed record EmailProcessingOutcome
{
    public int MessagesRead { get; init; }
    public int Matched { get; init; }
    public int StatusesChanged { get; init; }
    public int SuggestionsRaised { get; init; }
    public int Unrelated { get; init; }
    public int Failed { get; init; }
    public bool NotConfigured { get; init; }
}

/// <summary>Reads new mail, asks the AI to match each message to an open application, and
/// updates the status when it is confident. Below the confidence threshold it records a
/// suggestion instead, for me to confirm.
///
/// No email body is ever stored: only the sender, the subject and the AI's one-line
/// rationale are kept, and only on a suggestion.</summary>
public sealed class EmailProcessingService(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IEmailProvider emailProvider,
    IJobMatcher matcher,
    IOptions<JobScoutOptions> options,
    ILogger<EmailProcessingService> logger)
{
    private EmailOptions Config => options.Value.Email;

    public async Task<EmailProcessingOutcome> ProcessAsync(CancellationToken ct = default)
    {
        if (!emailProvider.IsConfigured)
        {
            logger.LogInformation("No mailbox is configured - the email check has nothing to do");
            return new EmailProcessingOutcome { NotConfigured = true };
        }

        var checkpoint = await LoadCheckpointAsync(ct);

        var fetch = await emailProvider.FetchSinceAsync(new EmailFetchCursor
        {
            Since = checkpoint.LastCheckedAt,
            UidValidity = checkpoint.UidValidity,
            LastUid = checkpoint.LastUid,
        }, ct);

        var openApplications = await LoadOpenApplicationsAsync(ct);

        var matched = 0;
        var changed = 0;
        var suggested = 0;
        var unrelated = 0;
        var failed = 0;

        if (openApplications.Count == 0 && fetch.Messages.Count > 0)
            logger.LogInformation("{Count} new message(s) but no open applications to match them against",
                fetch.Messages.Count);

        foreach (var message in fetch.Messages)
        {
            ct.ThrowIfCancellationRequested();

            if (openApplications.Count == 0)
            {
                unrelated++;
                continue;
            }

            EmailMatchResult? result;
            try
            {
                result = await matcher.ClassifyEmailAsync(
                    openApplications, message.FromAddress, message.Subject, message.BodySnippet, ct);
            }
            catch (Exception ex)
            {
                // Identify the message by UID only; its content must not reach the log.
                logger.LogWarning("Classifying message uid {Uid} failed: {Error}", message.Uid, ex.Message);
                failed++;
                continue;
            }

            if (result is null) { failed++; continue; }

            if (result.ApplicationId is null || result.Classification == EmailClassificationKind.Unrelated)
            {
                unrelated++;
                continue;
            }

            matched++;

            var applied = await ApplyAsync(result, message, ct);

            if (applied == ApplyResult.StatusChanged) changed++;
            else if (applied == ApplyResult.Suggested) suggested++;
        }

        await SaveCheckpointAsync(fetch, ct);

        logger.LogInformation(
            "Email check: {Read} read, {Matched} matched, {Changed} status change(s), {Suggested} suggestion(s)",
            fetch.Messages.Count, matched, changed, suggested);

        return new EmailProcessingOutcome
        {
            MessagesRead = fetch.Messages.Count,
            Matched = matched,
            StatusesChanged = changed,
            SuggestionsRaised = suggested,
            Unrelated = unrelated,
            Failed = failed,
        };
    }

    private enum ApplyResult { Nothing, StatusChanged, Suggested }

    private async Task<ApplyResult> ApplyAsync(EmailMatchResult result, EmailMessage message, CancellationToken ct)
    {
        var target = ApplicationStatusRules.FromClassification(result.Classification);
        if (target is null) return ApplyResult.Nothing;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var application = await db.Applications
            .FirstOrDefaultAsync(a => a.Id == result.ApplicationId!.Value, ct);

        if (application is null) return ApplyResult.Nothing;

        // Seeing any related mail is worth recording even when the status does not move.
        if (application.LastEmailAt is null || message.ReceivedAt > application.LastEmailAt)
            application.LastEmailAt = message.ReceivedAt;

        var confident = result.Confidence >= Config.AutoApplyConfidence;
        var allowed = ApplicationStatusRules.CanTransitionAutomatically(application.Status, target.Value);

        if (confident && allowed)
        {
            var from = application.Status;
            application.Status = target.Value;

            db.ApplicationStatusChanges.Add(new ApplicationStatusChange
            {
                JobApplicationId = application.Id,
                FromStatus = from,
                ToStatus = target.Value,
                ChangedAt = DateTimeOffset.UtcNow,
                Source = StatusChangeSource.EmailAutomatic,
                Note = Short(result.Rationale) ?? $"Email from {message.FromAddress}",
            });

            await db.SaveChangesAsync(ct);

            logger.LogInformation("Application {Id} moved {From} -> {To} from an email (confidence {Confidence:0.00})",
                application.Id, from, target.Value, result.Confidence);

            return ApplyResult.StatusChanged;
        }

        // Not confident enough, or the move would drag the application backwards.
        if (application.Status == target.Value)
        {
            await db.SaveChangesAsync(ct);
            return ApplyResult.Nothing;
        }

        var alreadyPending = await db.SuggestedStatusUpdates.AnyAsync(
            s => s.JobApplicationId == application.Id &&
                 s.SuggestedStatus == target.Value &&
                 !s.IsResolved, ct);

        if (alreadyPending)
        {
            await db.SaveChangesAsync(ct);
            return ApplyResult.Nothing;
        }

        db.SuggestedStatusUpdates.Add(new SuggestedStatusUpdate
        {
            JobApplicationId = application.Id,
            SuggestedStatus = target.Value,
            Confidence = result.Confidence,
            Rationale = Short(result.Rationale),
            EmailFrom = message.FromAddress,
            EmailSubject = Short(message.Subject),
            EmailReceivedAt = message.ReceivedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            IsResolved = false,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Suggested {To} for application {Id} (confidence {Confidence:0.00}) - waiting for confirmation",
            target.Value, application.Id, result.Confidence);

        return ApplyResult.Suggested;
    }

    private async Task<IReadOnlyList<(int Id, string Company, string Title)>> LoadOpenApplicationsAsync(
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var open = new[]
        {
            ApplicationStatus.Applied,
            ApplicationStatus.Acknowledged,
            ApplicationStatus.Interview,
            ApplicationStatus.Offer,
            // Silence is not an answer: a late reply must still reach the application.
            ApplicationStatus.NoResponse,
        };

        var rows = await db.Applications
            .AsNoTracking()
            .Where(a => open.Contains(a.Status))
            .OrderByDescending(a => a.AppliedAt)
            .Select(a => new { a.Id, a.CompanyName, a.Title })
            .ToListAsync(ct);

        return rows.Select(r => (r.Id, r.CompanyName, r.Title)).ToList();
    }

    private async Task<EmailCheckpoint> LoadCheckpointAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var key = emailProvider.MailboxKey;

        return await db.EmailCheckpoints.AsNoTracking().FirstOrDefaultAsync(c => c.MailboxKey == key, ct)
               ?? new EmailCheckpoint { MailboxKey = key };
    }

    private async Task SaveCheckpointAsync(EmailFetchResult fetch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var key = emailProvider.MailboxKey;
        var checkpoint = await db.EmailCheckpoints.FirstOrDefaultAsync(c => c.MailboxKey == key, ct);

        if (checkpoint is null)
        {
            checkpoint = new EmailCheckpoint { MailboxKey = key };
            db.EmailCheckpoints.Add(checkpoint);
        }

        checkpoint.LastCheckedAt = DateTimeOffset.UtcNow;
        checkpoint.UidValidity = fetch.UidValidity ?? checkpoint.UidValidity;

        // Never move the cursor backwards - a partial fetch must not skip messages.
        if (fetch.HighestUid is not null && fetch.HighestUid > (checkpoint.LastUid ?? 0))
            checkpoint.LastUid = fetch.HighestUid;

        await db.SaveChangesAsync(ct);
    }

    private static string? Short(string? value, int max = 480) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value.Trim()
        : value.Trim()[..max];
}

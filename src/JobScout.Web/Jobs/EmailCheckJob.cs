using JobScout.Core.Options;
using JobScout.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace JobScout.Web.Jobs;

/// <summary>Evening email check. Read-only against the mailbox: it never replies, moves,
/// flags or deletes anything. Afterwards it applies the no-response rule - after the mail, so
/// a reply that arrived today saves its application first.</summary>
public sealed class EmailCheckJob(
    EmailProcessingService emails,
    ApplicationService applications,
    IOptions<JobScoutOptions> options) : IScheduledJob
{
    public const string Key = "email-check";


    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        var outcome = await emails.ProcessAsync(ct);

        var parts = new List<string>();
        var issues = new List<string>();

        if (outcome.NotConfigured)
        {
            parts.Add("No mailbox configured - no mail read. Set JobScout:Email in user secrets to enable this");
        }
        else
        {
            parts.Add($"{outcome.MessagesRead} message(s) read");
            parts.Add($"{outcome.Matched} matched");
            parts.Add($"{outcome.StatusesChanged} status change(s)");
            parts.Add($"{outcome.SuggestionsRaised} suggestion(s) awaiting confirmation");

            if (outcome.Unrelated > 0) parts.Add($"{outcome.Unrelated} unrelated");
            if (outcome.Failed > 0) parts.Add($"{outcome.Failed} could not be classified");

            if (outcome.Failed > 0)
            {
                issues.Add(
                    $"The AI could not classify {outcome.Failed} of {outcome.MessagesRead} message(s), " +
                    "so any application updates they carried were missed.");
            }
        }

        // Runs whether or not a mailbox is configured: silence is silence either way.
        var days = options.Value.Applications.NoResponseAfterDays;

        if (days > 0)
        {
            var stale = await applications.MarkStaleAsync(days, ct: ct);
            parts.Add($"{stale} application(s) marked no response after {days} days");
        }

        return new JobRunResult(string.Join(", ", parts), issues);
    }
}

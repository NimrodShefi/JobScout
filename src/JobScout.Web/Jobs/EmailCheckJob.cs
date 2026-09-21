using JobScout.Infrastructure.Services;

namespace JobScout.Web.Jobs;

/// <summary>Evening email check. Read-only against the mailbox: it never replies, moves,
/// flags or deletes anything.</summary>
public sealed class EmailCheckJob(EmailProcessingService emails) : IScheduledJob
{
    public const string Key = "email-check";


    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        var outcome = await emails.ProcessAsync(ct);

        if (outcome.NotConfigured)
            return "No mailbox configured - nothing to do. Set JobScout:Email in user secrets to enable this.";

        var parts = new List<string>
        {
            $"{outcome.MessagesRead} message(s) read",
            $"{outcome.Matched} matched",
            $"{outcome.StatusesChanged} status change(s)",
            $"{outcome.SuggestionsRaised} suggestion(s) awaiting confirmation",
        };

        if (outcome.Unrelated > 0) parts.Add($"{outcome.Unrelated} unrelated");
        if (outcome.Failed > 0) parts.Add($"{outcome.Failed} could not be classified");

        var issues = new List<string>();

        if (outcome.Failed > 0)
        {
            issues.Add(
                $"The AI could not classify {outcome.Failed} of {outcome.MessagesRead} message(s), " +
                "so any application updates they carried were missed.");
        }

        return new JobRunResult(string.Join(", ", parts), issues);
    }
}

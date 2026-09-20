using JobScout.Infrastructure.Services;

namespace JobScout.Web.Jobs;

/// <summary>Evening email check. Read-only against the mailbox: it never replies, moves,
/// flags or deletes anything.</summary>
public sealed class EmailCheckJob(EmailProcessingService emails) : IScheduledJob
{
    public const string Key = "email-check";


    public async Task<string> RunAsync(CancellationToken ct)
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

        return string.Join(", ", parts);
    }
}

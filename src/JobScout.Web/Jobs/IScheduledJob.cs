namespace JobScout.Web.Jobs;

/// <summary>A job the scheduler can run on a cron schedule and that the UI can trigger
/// with a "Run now" button.
///
/// The display text lives on <see cref="JobCatalogue"/> rather than here, so the Job runs
/// page can list the jobs without constructing them - constructing one builds the AI client,
/// which fails when no API key is configured.</summary>
public interface IScheduledJob
{
    /// <summary>Does the work and returns the counts line written to JobRunLog, plus anything
    /// that went wrong along the way. Throwing is fine - the runner records the failure
    /// against the run.</summary>
    Task<JobRunResult> RunAsync(CancellationToken ct);
}

/// <summary>What a job hands back: the counts line for the runs table, and the problems it
/// carried on through.
///
/// Issues are not failures - the run still did its work - but they must reach the dashboard.
/// A job whose every AI call 400s returns "0 advert(s) found" for every company and finishes
/// green, which reads as a quiet morning rather than a broken pipeline.</summary>
public sealed record JobRunResult(string Summary, IReadOnlyList<string> Issues)
{
    public static implicit operator JobRunResult(string summary) => new(summary, []);
}

/// <summary>Static description of a schedulable job.</summary>
public sealed record JobDefinition(string Key, Type JobType, string DisplayName, string Description);

/// <summary>The jobs the scheduler knows about, in the order the UI lists them.</summary>
public static class JobCatalogue
{
    public static IReadOnlyList<JobDefinition> All { get; } =
    [
        new(MorningScanJob.Key, typeof(MorningScanJob), "Morning scan",
            "Read every USE company's careers page, save the new listings, then score anything unscored."),

        new(EmailCheckJob.Key, typeof(EmailCheckJob), "Email check",
            "Read new mail and update application statuses, then mark long-silent applications as no response. Read-only: nothing is ever sent, moved or deleted."),

        new(DiscoveryJob.Key, typeof(DiscoveryJob), "Discovery",
            "Query every enabled job board for each active industry and city. New companies land in REVIEW, unscored."),
    ];
}

using JobScout.Core.Enums;

namespace JobScout.Core.Entities;

/// <summary>One row per scheduled-job run. Summary only - detail goes to the Serilog files.</summary>
public class JobRunLog
{
    public int Id { get; set; }

    public string JobName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public bool Success { get; set; }

    /// <summary>Short counts line, e.g. "12 companies, 34 listings, 8 scored".</summary>
    public string? Summary { get; set; }

    /// <summary>Problems the run carried on through, one per line. A run can do its work and
    /// still have something badly wrong with it - an AI outage returns no adverts without
    /// throwing - so these are recorded separately from <see cref="Error"/> and shown in the UI.</summary>
    public string? Issues { get; set; }

    /// <summary>Exception message (not the full stack - that is in the log files).</summary>
    public string? Error { get; set; }

    /// <summary>True while the run is in flight.</summary>
    public bool IsRunning => FinishedAt is null;

    public bool HasIssues => !string.IsNullOrWhiteSpace(Issues);

    /// <summary>What the runs table shows. "Ok" means finished *and* clean.</summary>
    public JobRunOutcome Outcome =>
        IsRunning ? JobRunOutcome.Running
        : !Success ? JobRunOutcome.Failed
        : HasIssues ? JobRunOutcome.CompletedWithIssues
        : JobRunOutcome.Ok;

    public TimeSpan? Duration => FinishedAt is null ? null : FinishedAt - StartedAt;
}

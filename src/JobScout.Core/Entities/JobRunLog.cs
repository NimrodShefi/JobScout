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

    /// <summary>Exception message (not the full stack - that is in the log files).</summary>
    public string? Error { get; set; }

    /// <summary>True while the run is in flight.</summary>
    public bool IsRunning => FinishedAt is null;

    public TimeSpan? Duration => FinishedAt is null ? null : FinishedAt - StartedAt;
}

namespace JobScout.Core.Models;

/// <summary>The criteria block sent to the matcher alongside the CV and the advert.</summary>
public sealed record MatchCriteria
{
    public decimal MinimumSalary { get; init; }
    public string Currency { get; init; } = "GBP";
    public IReadOnlyList<string> Cities { get; init; } = [];
    public bool IncludeRemote { get; init; } = true;

    /// <summary>Titles I am looking for. Empty means every title is in scope.</summary>
    public IReadOnlyList<string> DesiredRoles { get; init; } = [];

    /// <summary>Words that rule a title out. Always beats <see cref="DesiredRoles"/>.</summary>
    public IReadOnlyList<string> ExcludedRoles { get; init; } = [];
}

/// <summary>What the AI must return for a scored job. Validated before use.</summary>
public sealed record JobMatchResult
{
    /// <summary>0-100.</summary>
    public int Score { get; init; }

    public string Reasoning { get; init; } = string.Empty;
    public IReadOnlyList<string> Strengths { get; init; } = [];
    public IReadOnlyList<string> Gaps { get; init; } = [];

    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }

    public string? Location { get; init; }
    public bool IsRemote { get; init; }
}

/// <summary>A job the AI pulled out of a careers page.</summary>
public sealed record ExtractedJob
{
    public string Title { get; init; } = string.Empty;
    public string? Location { get; init; }

    /// <summary>Absolute URL to the advert. Relative URLs are resolved before this is returned.</summary>
    public string Url { get; init; } = string.Empty;

    public string? Description { get; init; }
    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public bool IsRemote { get; init; }
}

/// <summary>Classification of one inbound email against my open applications.</summary>
public sealed record EmailMatchResult
{
    /// <summary>Id of the matched application, or null when nothing matched.</summary>
    public int? ApplicationId { get; init; }

    public EmailClassificationKind Classification { get; init; } = EmailClassificationKind.Unrelated;

    /// <summary>0-1. Below the configured threshold the result becomes a suggestion only.</summary>
    public double Confidence { get; init; }

    /// <summary>One short line. Must not quote the email body.</summary>
    public string? Rationale { get; init; }
}

public enum EmailClassificationKind
{
    Unrelated = 0,
    Acknowledged = 1,
    Interview = 2,
    Rejection = 3,
    Offer = 4,
}

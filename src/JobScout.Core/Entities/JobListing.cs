using JobScout.Core.Enums;

namespace JobScout.Core.Entities;

/// <summary>A single job advert. <see cref="Url"/> is the dedupe key.</summary>
public class JobListing
{
    public int Id { get; set; }

    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Location { get; set; }

    /// <summary>Canonical advert URL. Unique index — this is how we dedupe across runs and sources.</summary>
    public string Url { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string? SalaryCurrency { get; set; }

    /// <summary>False when no salary could be found. Such listings are kept, not filtered out.</summary>
    public bool SalaryKnown { get; set; }

    /// <summary>"CareerPage" or the board name, e.g. "Adzuna".</summary>
    public string Source { get; set; } = "CareerPage";

    public DateTimeOffset FoundAt { get; set; }

    /// <summary>Date the advert was posted, when the source tells us.</summary>
    public DateTimeOffset? PostedAt { get; set; }

    /// <summary>0-100. Null until scored.</summary>
    public int? Score { get; set; }

    public string? Reasoning { get; set; }
    public string? Strengths { get; set; }
    public string? Gaps { get; set; }

    public DateTimeOffset? ScoredAt { get; set; }

    /// <summary>Hash of the content that was scored, so unchanged listings are not rescored.</summary>
    public string? ScoredContentHash { get; set; }

    public JobListingStatus Status { get; set; } = JobListingStatus.New;

    public bool IsRemote { get; set; }
}

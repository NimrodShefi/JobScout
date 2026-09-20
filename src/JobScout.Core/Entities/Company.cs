using JobScout.Core.Enums;

namespace JobScout.Core.Entities;

/// <summary>An employer. Uniqueness is by <see cref="NormalisedName"/> so that
/// "Acme Ltd." discovered from a board does not duplicate a manual "Acme Ltd".</summary>
public class Company
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-cased, punctuation- and suffix-stripped name. Unique index.</summary>
    public string NormalisedName { get; set; } = string.Empty;

    /// <summary>The company's own careers page. Null until I fill it in.</summary>
    public string? Url { get; set; }

    /// <summary>Link back to the company/listing on the board it was discovered from.</summary>
    public string? BoardUrl { get; set; }

    /// <summary>"Manual" or the job board name, e.g. "Adzuna".</summary>
    public string Source { get; set; } = "Manual";

    public CompanyStatus Status { get; set; } = CompanyStatus.REVIEW;

    public DateTimeOffset DiscoveredAt { get; set; }

    /// <summary>Last time the careers page was successfully fetched.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    public string? Notes { get; set; }

    public List<JobListing> Listings { get; set; } = [];

    /// <summary>True when the company is in scope for scanning but has no careers page yet.</summary>
    public bool NeedsCareersUrl => Status == CompanyStatus.USE && string.IsNullOrWhiteSpace(Url);
}

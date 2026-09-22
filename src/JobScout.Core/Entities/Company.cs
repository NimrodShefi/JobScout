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

    /// <summary>The job-board service behind the careers page, when there is one.</summary>
    public AtsKind AtsKind { get; set; } = AtsKind.None;

    /// <summary>The company's identifier on that service, e.g. "monzo" in
    /// boards.greenhouse.io/monzo.</summary>
    public string? AtsToken { get; set; }

    /// <summary>When automatic feed detection last looked at this company, so a company with
    /// no feed is not probed again every morning.</summary>
    public DateTimeOffset? AtsCheckedAt { get; set; }

    public List<JobListing> Listings { get; set; } = [];

    /// <summary>True when the adverts can be read from a job-board feed.</summary>
    public bool HasAtsFeed => AtsKind != AtsKind.None && !string.IsNullOrWhiteSpace(AtsToken);

    /// <summary>True when the company is in scope for scanning but there is nothing to scan:
    /// no careers page and no job-board feed.</summary>
    public bool NeedsCareersUrl => Status == CompanyStatus.USE && string.IsNullOrWhiteSpace(Url) && !HasAtsFeed;
}

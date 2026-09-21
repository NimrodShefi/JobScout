using JobScout.Core.Models;

namespace JobScout.Core.Abstractions;

/// <summary>AI-backed scoring and extraction. Implemented on Microsoft.Extensions.AI
/// so any chat provider works.</summary>
public interface IJobMatcher
{
    /// <summary>Score one advert against my CV and criteria. Returns null if the model
    /// could not produce valid JSON after the configured retries.</summary>
    Task<JobMatchResult?> ScoreAsync(
        string cvText,
        MatchCriteria criteria,
        string jobTitle,
        string? jobLocation,
        string jobDescription,
        CancellationToken ct = default);

    /// <summary>Pull job adverts out of a careers page. An empty list means the page held
    /// no adverts; null means the call itself failed after the configured retries, which is
    /// a very different thing and must not be reported as a quiet day.</summary>
    Task<IReadOnlyList<ExtractedJob>?> ExtractJobsAsync(
        string pageText,
        string pageUrl,
        string companyName,
        CancellationToken ct = default);

    /// <summary>Match one email to an open application and classify it.
    /// <paramref name="openApplications"/> carries ids, company and title only.</summary>
    Task<EmailMatchResult?> ClassifyEmailAsync(
        IReadOnlyList<(int Id, string Company, string Title)> openApplications,
        string fromAddress,
        string? subject,
        string? bodySnippet,
        CancellationToken ct = default);
}

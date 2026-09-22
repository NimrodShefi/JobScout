using JobScout.Core.Enums;
using JobScout.Core.Models;

namespace JobScout.Core.Abstractions;

/// <summary>A hosted job-board service's public JSON feed (Greenhouse, Lever, Ashby,
/// Workable). Reading it replaces scraping the careers page: the adverts arrive structured,
/// with full descriptions, and no AI call is needed to pull them out.</summary>
public interface IAtsFeed
{
    AtsKind Kind { get; }

    /// <summary>Every advert currently on the board. An empty list means the board exists but
    /// has nothing open; null means the board could not be read - including when the
    /// service does not know the token, which is what detection relies on.</summary>
    Task<IReadOnlyList<ExtractedJob>?> FetchAsync(string token, CancellationToken ct = default);
}

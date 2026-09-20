using JobScout.Core.Models;

namespace JobScout.Core.Abstractions;

/// <summary>Fetches a page politely: robots.txt, timeouts, delays, concurrency limit,
/// and a browser fallback when the page needs JavaScript.</summary>
public interface IPageFetcher
{
    Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default);
}

/// <summary>Headless-browser fetch. Separate so it can be swapped or stubbed out,
/// and so the app runs without Playwright browsers installed.</summary>
public interface IBrowserPageFetcher
{
    Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default);
}

/// <summary>Caches and evaluates robots.txt per host.</summary>
public interface IRobotsGate
{
    Task<bool> IsAllowedAsync(string url, CancellationToken ct = default);
}

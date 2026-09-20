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

/// <summary>What a host's robots.txt says about one URL.</summary>
/// <param name="IsAllowed">False when a Disallow rule covers this path.</param>
/// <param name="CrawlDelay">The host's declared Crawl-delay, when it publishes one.</param>
/// <param name="WasRead">
/// False when robots.txt could not be read at all, so the rules are unknown rather than absent.
/// </param>
public sealed record RobotsPolicy(bool IsAllowed, TimeSpan? CrawlDelay, bool WasRead)
{
    public static RobotsPolicy AllowUnknown() => new(true, null, false);
}

/// <summary>Caches and evaluates robots.txt per host.</summary>
public interface IRobotsGate
{
    Task<RobotsPolicy> GetPolicyAsync(string url, CancellationToken ct = default);
}

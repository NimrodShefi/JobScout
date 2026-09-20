using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using JobScout.Core.Abstractions;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Fetching;

/// <summary>Fetches and caches robots.txt per host and answers allow/deny for a path,
/// along with the host's declared Crawl-delay.
///
/// A host that does not publish robots.txt is treated as allowing everything, which is what
/// RFC 9309 says. A host that *blocks* our HTTP client from reading robots.txt is a different
/// case: its rules exist and we simply cannot see them. Assuming allow-all there would mean
/// ignoring real Disallow rules, so we retry through the headless browser first.</summary>
public sealed class RobotsGate(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<RobotsGate> logger,
    IBrowserPageFetcher? browser = null) : IRobotsGate
{
    public const string HttpClientName = "robots";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    /// <summary>Statuses that mean "we were refused", as opposed to "there is nothing here".</summary>
    private static readonly HttpStatusCode[] BlockedStatuses =
    [
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.TooManyRequests,
    ];

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedRules>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private FetchOptions Fetching => options.Value.Fetching;

    public async Task<RobotsPolicy> GetPolicyAsync(string url, CancellationToken ct = default)
    {
        if (!Fetching.RespectRobotsTxt)
            return RobotsPolicy.AllowUnknown();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new RobotsPolicy(false, null, WasRead: false);

        var rules = await GetRulesAsync(uri, ct);
        var allowed = rules.IsAllowed(uri.AbsolutePath);

        if (!allowed)
            logger.LogInformation("robots.txt on {Host} disallows {Path}", uri.Host, uri.AbsolutePath);

        return new RobotsPolicy(allowed, CappedCrawlDelay(rules.CrawlDelay), rules.WasRead);
    }

    /// <summary>Honours a declared Crawl-delay but refuses to be stalled indefinitely by one.</summary>
    private TimeSpan? CappedCrawlDelay(TimeSpan? declared)
    {
        if (declared is null) return null;

        var cap = TimeSpan.FromSeconds(Math.Max(1, Fetching.MaxCrawlDelaySeconds));
        return declared.Value > cap ? cap : declared;
    }

    private async Task<CachedRules> GetRulesAsync(Uri uri, CancellationToken ct)
    {
        var key = uri.GetLeftPart(UriPartial.Authority);

        while (true)
        {
            var entry = _cache.GetOrAdd(key, k =>
                new Lazy<Task<CachedRules>>(() => LoadAsync(k, ct), LazyThreadSafetyMode.ExecutionAndPublication));

            var rules = await entry.Value;

            if (rules.FetchedAt + CacheLifetime > DateTimeOffset.UtcNow)
                return rules;

            // Expired - drop this entry and let the next caller reload it.
            _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CachedRules>>>(key, entry));
        }
    }

    private async Task<CachedRules> LoadAsync(string authority, CancellationToken ct)
    {
        var robotsUrl = authority.TrimEnd('/') + "/robots.txt";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(robotsUrl, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return CachedRules.Parse(body, Fetching.UserAgent);
            }

            if (BlockedStatuses.Contains(response.StatusCode))
            {
                // Refused, not absent. Try to read the real rules rather than assume none.
                var viaBrowser = await LoadThroughBrowserAsync(robotsUrl, (int)response.StatusCode, ct);
                if (viaBrowser is not null) return viaBrowser;
            }
            else
            {
                logger.LogDebug("No robots.txt at {Url} ({Status}) - allowing", robotsUrl, (int)response.StatusCode);
            }

            return CachedRules.Unknown();
        }
        catch (Exception ex)
        {
            // An unreachable robots.txt must not stop the whole run.
            logger.LogDebug("Could not read {Url} ({Error}) - allowing", robotsUrl, ex.Message);
            return CachedRules.Unknown();
        }
    }

    private async Task<CachedRules?> LoadThroughBrowserAsync(string robotsUrl, int status, CancellationToken ct)
    {
        if (browser is null || !Fetching.EnableBrowserFallback || !Fetching.RetryBlockedPagesWithBrowser)
        {
            logger.LogWarning(
                "robots.txt at {Url} returned {Status}, so this host's crawl rules are unknown. " +
                "Enable Fetching:RetryBlockedPagesWithBrowser to read them with the headless browser.",
                robotsUrl, status);

            return null;
        }

        var result = await browser.FetchAsync(robotsUrl, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
        {
            logger.LogWarning("robots.txt at {Url} returned {Status} and could not be read with the browser either",
                robotsUrl, status);
            return null;
        }

        var rules = CachedRules.Parse(result.Text, Fetching.UserAgent);

        logger.LogInformation(
            "robots.txt at {Url} refused our HTTP client ({Status}) but was read with the browser: " +
            "{DisallowCount} disallow rule(s), crawl delay {CrawlDelay}",
            robotsUrl, status, rules.Disallow.Count,
            rules.CrawlDelay?.ToString() ?? "not declared");

        return rules;
    }

    /// <summary>Parsed Allow/Disallow rules for the group that applies to us.</summary>
    internal sealed record CachedRules(
        IReadOnlyList<string> Allow,
        IReadOnlyList<string> Disallow,
        TimeSpan? CrawlDelay,
        bool WasRead,
        DateTimeOffset FetchedAt)
    {
        /// <summary>No rules were readable, so nothing is known and nothing is blocked.</summary>
        public static CachedRules Unknown() => new([], [], null, false, DateTimeOffset.UtcNow);

        /// <summary>Longest matching rule wins; Allow beats Disallow at equal length,
        /// which is how the major crawlers resolve it.</summary>
        public bool IsAllowed(string path)
        {
            if (Disallow.Count == 0) return true;

            var bestDisallow = Disallow.Where(r => Matches(r, path)).Select(r => r.Length).DefaultIfEmpty(-1).Max();
            if (bestDisallow < 0) return true;

            var bestAllow = Allow.Where(r => Matches(r, path)).Select(r => r.Length).DefaultIfEmpty(-1).Max();
            return bestAllow >= bestDisallow;
        }

        private static bool Matches(string rule, string path)
        {
            if (rule.Length == 0) return false;

            // Support the two wildcards the de-facto standard defines.
            if (!rule.Contains('*') && !rule.EndsWith('$'))
                return path.StartsWith(rule, StringComparison.OrdinalIgnoreCase);

            var anchored = rule.EndsWith('$');
            var pattern = anchored ? rule[..^1] : rule;
            var segments = pattern.Split('*');

            var index = 0;
            for (var s = 0; s < segments.Length; s++)
            {
                var segment = segments[s];
                if (segment.Length == 0) continue;

                var found = s == 0
                    ? (path.StartsWith(segment, StringComparison.OrdinalIgnoreCase) ? 0 : -1)
                    : path.IndexOf(segment, index, StringComparison.OrdinalIgnoreCase);

                if (found < 0) return false;
                index = found + segment.Length;
            }

            return !anchored || index == path.Length;
        }

        public static CachedRules Parse(string body, string ourUserAgent)
        {
            // Two groups are relevant: one naming us, and the "*" group. A group naming
            // us wins outright; otherwise the "*" group applies.
            var star = new List<(bool IsAllow, string Path)>();
            var mine = new List<(bool IsAllow, string Path)>();

            TimeSpan? starDelay = null;
            TimeSpan? myDelay = null;

            // Consecutive User-agent lines share one group, so a group can feed both lists.
            var active = new List<List<(bool IsAllow, string Path)>>();
            var agentToken = AgentToken(ourUserAgent);
            var lastLineWasAgent = false;

            foreach (var raw in body.Split('\n'))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var field = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();

                if (field == "user-agent")
                {
                    if (!lastLineWasAgent) active.Clear();

                    if (value == "*")
                        active.Add(star);
                    else if (agentToken.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                             value.Contains(agentToken, StringComparison.OrdinalIgnoreCase))
                        active.Add(mine);

                    lastLineWasAgent = true;
                    continue;
                }

                lastLineWasAgent = false;

                if (active.Count == 0 || value.Length == 0) continue;

                if (field is "disallow" or "allow")
                {
                    var rule = (field == "allow", value);
                    foreach (var target in active) target.Add(rule);
                }
                else if (field == "crawl-delay" && TryParseDelay(value, out var delay))
                {
                    if (active.Contains(mine)) myDelay = delay;
                    if (active.Contains(star)) starDelay = delay;
                }
            }

            var useMine = mine.Count > 0 || myDelay is not null;
            var chosen = useMine ? mine : star;

            return new CachedRules(
                chosen.Where(r => r.IsAllow).Select(r => r.Path).ToList(),
                chosen.Where(r => !r.IsAllow).Select(r => r.Path).ToList(),
                useMine ? myDelay ?? starDelay : starDelay,
                WasRead: true,
                DateTimeOffset.UtcNow);
        }

        /// <summary>Crawl-delay is in seconds and is often fractional.</summary>
        private static bool TryParseDelay(string value, out TimeSpan delay)
        {
            delay = default;

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;

            if (seconds is <= 0 or > 86_400) return false;

            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        /// <summary>"JobScout/1.0 (...)" becomes "jobscout".</summary>
        private static string AgentToken(string userAgent)
        {
            var first = userAgent.Split(' ', '/')[0];
            return string.IsNullOrWhiteSpace(first) ? "jobscout" : first.ToLowerInvariant();
        }
    }
}

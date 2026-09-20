using System.Collections.Concurrent;
using JobScout.Core.Abstractions;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Fetching;

/// <summary>Fetches and caches robots.txt per host and answers allow/deny for a path.
/// A host that does not serve robots.txt is treated as allowing everything, which is
/// what the standard says.</summary>
public sealed class RobotsGate(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<RobotsGate> logger) : IRobotsGate
{
    public const string HttpClientName = "robots";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedRules>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsAllowedAsync(string url, CancellationToken ct = default)
    {
        if (!options.Value.Fetching.RespectRobotsTxt)
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        var rules = await GetRulesAsync(uri, ct);
        var allowed = rules.IsAllowed(uri.AbsolutePath);

        if (!allowed)
            logger.LogInformation("robots.txt on {Host} disallows {Path}", uri.Host, uri.AbsolutePath);

        return allowed;
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

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("No usable robots.txt at {Url} ({Status}) - allowing", robotsUrl, (int)response.StatusCode);
                return CachedRules.AllowAll();
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return CachedRules.Parse(body, options.Value.Fetching.UserAgent);
        }
        catch (Exception ex)
        {
            // Unreachable robots.txt must not block the whole run.
            logger.LogDebug("Could not read {Url} ({Error}) - allowing", robotsUrl, ex.Message);
            return CachedRules.AllowAll();
        }
    }

    /// <summary>Parsed Allow/Disallow rules for the group that applies to us.</summary>
    internal sealed record CachedRules(
        IReadOnlyList<string> Allow,
        IReadOnlyList<string> Disallow,
        DateTimeOffset FetchedAt)
    {
        public static CachedRules AllowAll() => new([], [], DateTimeOffset.UtcNow);

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
            }

            var chosen = mine.Count > 0 ? mine : star;

            return new CachedRules(
                chosen.Where(r => r.IsAllow).Select(r => r.Path).ToList(),
                chosen.Where(r => !r.IsAllow).Select(r => r.Path).ToList(),
                DateTimeOffset.UtcNow);
        }

        /// <summary>"JobScout/1.0 (...)" becomes "jobscout".</summary>
        private static string AgentToken(string userAgent)
        {
            var first = userAgent.Split(' ', '/')[0];
            return string.IsNullOrWhiteSpace(first) ? "jobscout" : first.ToLowerInvariant();
        }
    }
}

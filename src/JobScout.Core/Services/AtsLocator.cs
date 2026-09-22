using System.Net;
using System.Text.RegularExpressions;
using JobScout.Core.Enums;

namespace JobScout.Core.Services;

/// <summary>A company's board on one job-board service.</summary>
public sealed record AtsReference(AtsKind Kind, string Token);

/// <summary>Works out which job-board service, if any, is behind a careers page: from the
/// careers URL itself, from links and embeds in the page's HTML, or - as a last resort - by
/// guessing the token from the company name. Pure, so it is cheap to test; confirming a
/// candidate against the live feed is the caller's job.</summary>
public static partial class AtsLocator
{
    // Path segments that are part of a service's own URL scheme rather than a company token.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "embed", "api", "v0", "v1", "j", "jobs", "job_board", "posting-api", "job-board",
        "boards", "widget", "accounts", "careers", "www",
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex TokenShape();

    [GeneratedRegex(
        @"(?:https?:)?//[a-z0-9.-]*(?:greenhouse\.io|lever\.co|ashbyhq\.com|workable\.com)[^\s""'<>()\\]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ServiceUrl();

    /// <summary>Reads the service and token out of a URL on one of the supported services,
    /// e.g. https://jobs.lever.co/acme/1234 or boards.greenhouse.io/embed/job_board?for=acme.
    /// Returns null for anything else.</summary>
    public static AtsReference? TryParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var candidate = url.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return host switch
        {
            // The EU data centres use different API hosts, which the feeds do not call.
            _ when host.Contains(".eu.", StringComparison.Ordinal) => null,

            "boards.greenhouse.io" or "job-boards.greenhouse.io" =>
                GreenhouseEmbedToken(uri) is { } embedded
                    ? Make(AtsKind.Greenhouse, embedded)
                    : Make(AtsKind.Greenhouse, First(segments)),

            "boards-api.greenhouse.io" =>
                Make(AtsKind.Greenhouse, After(segments, "boards")),

            "jobs.lever.co" => Make(AtsKind.Lever, First(segments)),
            "api.lever.co" => Make(AtsKind.Lever, After(segments, "postings")),

            "jobs.ashbyhq.com" => Make(AtsKind.Ashby, First(segments)),
            "api.ashbyhq.com" => Make(AtsKind.Ashby, After(segments, "job-board")),

            "apply.workable.com" => segments.Length >= 4 && segments[0] == "api"
                ? Make(AtsKind.Workable, After(segments, "accounts"))
                : Make(AtsKind.Workable, First(segments)),

            // Deliberately not the old acme.workable.com form: it redirects to
            // apply.workable.com anyway, and would also match Workable's own subdomains
            // (resources., help.) that a careers page might link to.
            _ => null,
        };
    }

    /// <summary>Every distinct board referenced by a page's links, scripts and iframes, in
    /// the order they first appear. A careers page that embeds its Greenhouse board or links
    /// each advert to jobs.lever.co gives itself away here.</summary>
    public static IReadOnlyList<AtsReference> FindInHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var found = new List<AtsReference>();
        var seen = new HashSet<(AtsKind, string)>();

        foreach (Match m in ServiceUrl().Matches(html))
        {
            var reference = TryParseUrl(WebUtility.HtmlDecode(m.Value));
            if (reference is null) continue;

            if (seen.Add((reference.Kind, reference.Token.ToLowerInvariant())))
                found.Add(reference);
        }

        return found;
    }

    /// <summary>Tokens worth trying when nothing on the page names the board: the normalised
    /// name run together ("fundingcircle") and hyphenated ("funding-circle"). Services let
    /// companies pick any token, so this finds some boards and misses others (Wise is
    /// "transferwise"); a match is confirmed against the live feed before it is trusted.</summary>
    public static IReadOnlyList<string> GuessTokens(string? companyName)
    {
        var normalised = CompanyNameNormaliser.Normalise(companyName);
        if (normalised.Length == 0) return [];

        var words = normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Concat(words);

        // Very short tokens collide with unrelated companies too easily to be worth guessing.
        if (joined.Length < 3) return [];

        var tokens = new List<string> { joined };
        if (words.Length > 1) tokens.Add(string.Join('-', words));

        return tokens.Where(t => TokenShape().IsMatch(t)).ToList();
    }

    private static string? GreenhouseEmbedToken(Uri uri)
    {
        if (!uri.AbsolutePath.StartsWith("/embed", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("for", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }

    private static string? First(string[] segments) => segments.Length > 0 ? segments[0] : null;

    private static string? After(string[] segments, string marker)
    {
        var i = Array.FindIndex(segments, s => s.Equals(marker, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < segments.Length ? segments[i + 1] : null;
    }

    private static AtsReference? Make(AtsKind kind, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        token = Uri.UnescapeDataString(token.Trim());

        if (Reserved.Contains(token) || !TokenShape().IsMatch(token)) return null;

        return new AtsReference(kind, token);
    }
}

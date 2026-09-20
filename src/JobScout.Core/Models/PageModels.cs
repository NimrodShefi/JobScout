namespace JobScout.Core.Models;

/// <summary>Result of fetching a web page.</summary>
public sealed record PageFetchResult
{
    public required string Url { get; init; }
    public bool Success { get; init; }

    /// <summary>Raw HTML, or null on failure.</summary>
    public string? Html { get; init; }

    /// <summary>Visible text with script/style stripped. Used for the JS-rendering heuristic and for the AI.</summary>
    public string? Text { get; init; }

    /// <summary>"HttpClient" or "Playwright".</summary>
    public string Method { get; init; } = "HttpClient";

    public int? StatusCode { get; init; }
    public string? Error { get; init; }

    /// <summary>True when robots.txt told us not to fetch this path.</summary>
    public bool BlockedByRobots { get; init; }

    /// <summary>True when the host refused the request outright - 401, 403 or 429. Typically
    /// edge bot protection rejecting a client that does not look like a browser, rather than
    /// a considered decision about this particular path.</summary>
    public bool BlockedByBotProtection =>
        StatusCode is 401 or 403 or 429;

    public static PageFetchResult Failed(string url, string error, string method = "HttpClient") =>
        new() { Url = url, Success = false, Error = error, Method = method };
}

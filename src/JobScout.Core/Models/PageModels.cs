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

    public static PageFetchResult Failed(string url, string error, string method = "HttpClient") =>
        new() { Url = url, Success = false, Error = error, Method = method };
}

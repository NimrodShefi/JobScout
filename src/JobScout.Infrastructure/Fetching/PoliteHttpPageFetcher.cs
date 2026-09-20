using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Fetching;

/// <summary>The default fetcher: robots.txt first, then plain HTTP, then Playwright only
/// when the HTML looks like an unrendered SPA shell. Concurrency is capped globally and
/// a per-host delay keeps us polite.</summary>
public sealed class PoliteHttpPageFetcher : IPageFetcher, IDisposable
{
    public const string HttpClientName = "pages";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRobotsGate _robots;
    private readonly IBrowserPageFetcher? _browser;
    private readonly IOptions<JobScoutOptions> _options;
    private readonly ILogger<PoliteHttpPageFetcher> _logger;

    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAllowedPerHost = new(StringComparer.OrdinalIgnoreCase);

    public PoliteHttpPageFetcher(
        IHttpClientFactory httpClientFactory,
        IRobotsGate robots,
        IOptions<JobScoutOptions> options,
        ILogger<PoliteHttpPageFetcher> logger,
        IBrowserPageFetcher? browser = null)
    {
        _httpClientFactory = httpClientFactory;
        _robots = robots;
        _options = options;
        _logger = logger;
        _browser = browser;
        _concurrency = new SemaphoreSlim(Math.Max(1, options.Value.Fetching.MaxConcurrency));
    }

    private FetchOptions Fetching => _options.Value.Fetching;

    public async Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return PageFetchResult.Failed(url, "Not an absolute http(s) URL.");

        if (!await _robots.IsAllowedAsync(url, ct))
        {
            return new PageFetchResult
            {
                Url = url,
                Success = false,
                BlockedByRobots = true,
                Error = "Disallowed by robots.txt.",
            };
        }

        await _concurrency.WaitAsync(ct);
        try
        {
            await WaitForHostTurnAsync(uri.Host, ct);

            var result = await FetchOverHttpAsync(uri, ct);

            if (!ShouldTryBrowser(result))
                return result;

            _logger.LogInformation("{Url} looks JavaScript-rendered - retrying with a headless browser", url);

            var viaBrowser = await _browser!.FetchAsync(url, ct);

            // Only prefer the browser result if it actually gave us more to work with.
            if (viaBrowser.Success && (viaBrowser.Text?.Length ?? 0) > (result.Text?.Length ?? 0))
                return viaBrowser;

            if (viaBrowser.Success) return viaBrowser;

            _logger.LogDebug("Browser fallback for {Url} failed ({Error}) - keeping the HTTP result",
                url, viaBrowser.Error);

            return result;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private bool ShouldTryBrowser(PageFetchResult httpResult)
    {
        if (_browser is null || !Fetching.EnableBrowserFallback) return false;
        if (httpResult.BlockedByRobots) return false;

        // A transport failure is worth one browser attempt; a 4xx is not.
        if (!httpResult.Success)
            return httpResult.StatusCode is null;

        return HtmlText.LooksJavaScriptRendered(
            httpResult.Html ?? string.Empty,
            httpResult.Text ?? string.Empty,
            Fetching.JsHeuristicMinTextLength);
    }

    private async Task<PageFetchResult> FetchOverHttpAsync(Uri uri, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GET {Url} returned {Status}", uri, (int)response.StatusCode);
                return new PageFetchResult
                {
                    Url = uri.ToString(),
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                };
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (contentType.Length > 0 && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                                       && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                                       && !contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return new PageFetchResult
                {
                    Url = uri.ToString(),
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Error = $"Unsupported content type '{contentType}'.",
                };
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var text = HtmlText.ExtractTextWithLinks(html, uri.ToString(), Fetching.MaxPageTextChars);

            _logger.LogDebug("GET {Url} -> {Status}, {Chars} chars of text in {Ms}ms",
                uri, (int)response.StatusCode, text.Length, stopwatch.ElapsedMilliseconds);

            return new PageFetchResult
            {
                Url = uri.ToString(),
                Success = true,
                Html = html,
                Text = text,
                Method = "HttpClient",
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("GET {Url} failed: {Error}", uri, ex.Message);
            return PageFetchResult.Failed(uri.ToString(), ex.Message);
        }
    }

    /// <summary>Spaces out requests to the same host by the configured delay.</summary>
    private async Task WaitForHostTurnAsync(string host, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, Fetching.PolitenessDelayMs));
        if (delay <= TimeSpan.Zero) return;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var current = _nextAllowedPerHost.GetOrAdd(host, now);

            if (current <= now)
            {
                if (_nextAllowedPerHost.TryUpdate(host, now + delay, current))
                    return;

                continue; // Another fetch claimed the slot - re-read and try again.
            }

            var wait = current - now;
            if (_nextAllowedPerHost.TryUpdate(host, current + delay, current))
            {
                await Task.Delay(wait, ct);
                return;
            }
        }
    }

    public void Dispose() => _concurrency.Dispose();

    /// <summary>Shared by the page and robots HttpClients. Timeouts are left to the
    /// resilience pipeline, which owns both the per-attempt and the total budget; an
    /// HttpClient.Timeout on top of it would cut retries short.</summary>
    public static void ConfigureClient(HttpClient client, FetchOptions fetching)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(fetching.UserAgent);
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
    }

    public static HttpClientHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    };
}

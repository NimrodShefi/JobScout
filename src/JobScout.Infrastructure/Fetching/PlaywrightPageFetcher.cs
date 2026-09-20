using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace JobScout.Infrastructure.Fetching;

/// <summary>Headless Chromium fallback for pages that build their job list in the browser.
/// The browser is launched lazily and reused, so a run that never needs it costs nothing.
/// If the browser binaries are not installed, this reports a clear error instead of throwing
/// the whole run away.</summary>
public sealed class PlaywrightPageFetcher(
    IOptions<JobScoutOptions> options,
    ILogger<PlaywrightPageFetcher> logger) : IBrowserPageFetcher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _unavailable;
    private string? _unavailableReason;

    private FetchOptions Fetching => options.Value.Fetching;

    public async Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Fetching.EnableBrowserFallback)
            return PageFetchResult.Failed(url, "Browser fallback is disabled in configuration.", "Playwright");

        if (_unavailable)
            return PageFetchResult.Failed(url, _unavailableReason ?? "Playwright is unavailable.", "Playwright");

        IBrowser browser;
        try
        {
            browser = await GetBrowserAsync(ct);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _unavailableReason =
                "Could not start Playwright. Install the browsers with: " +
                "pwsh src/JobScout.Web/bin/Debug/net10.0/playwright.ps1 install chromium. " +
                $"Original error: {ex.Message}";

            logger.LogWarning("Playwright unavailable, disabling browser fallback for this process: {Error}", ex.Message);
            return PageFetchResult.Failed(url, _unavailableReason, "Playwright");
        }

        IBrowserContext? context = null;
        try
        {
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = Fetching.UserAgent,
                Locale = "en-GB",
                JavaScriptEnabled = true,
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(Fetching.TimeoutSeconds * 1000);

            // Images and fonts add nothing here and cost time.
            await page.RouteAsync("**/*", async route =>
            {
                var type = route.Request.ResourceType;
                if (type is "image" or "font" or "media")
                    await route.AbortAsync();
                else
                    await route.ContinueAsync();
            });

            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = Fetching.TimeoutSeconds * 1000,
            });

            var html = await page.ContentAsync();
            var text = HtmlText.ExtractTextWithLinks(html, url, Fetching.MaxPageTextChars);

            logger.LogDebug("Playwright fetched {Url} -> {Chars} chars of text", url, text.Length);

            return new PageFetchResult
            {
                Url = url,
                Success = true,
                Html = html,
                Text = text,
                Method = "Playwright",
                StatusCode = response?.Status,
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug("Playwright failed on {Url}: {Error}", url, ex.Message);
            return PageFetchResult.Failed(url, ex.Message, "Playwright");
        }
        finally
        {
            if (context is not null)
                await context.CloseAsync();
        }
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true }) return _browser;

        await _lock.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;

            _playwright ??= await Playwright.CreateAsync();

            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            });

            logger.LogInformation("Headless Chromium started for JavaScript-rendered pages");
            return _browser;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();
        _lock.Dispose();
    }
}

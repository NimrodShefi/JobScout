using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Infrastructure.Fetching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>What happens when a site answers our plain HTTP client with 403.
///
/// Large careers sites sit behind bot protection that rejects anything not shaped like a
/// browser. The policy is: robots.txt still decides what may be fetched, and only when it
/// permits the path do we retry through real Chromium.</summary>
public class BlockedPageFetchTests
{
    private const string Url = "https://jobs.example.test/search/technology/jobs/in/london";

    private static PoliteHttpPageFetcher Build(
        StubHttpHandler handler,
        StubBrowser browser,
        StubRobots robots,
        bool retryBlocked = true,
        bool enableBrowser = true)
    {
        var options = Options.Create(new JobScoutOptions
        {
            Fetching = new FetchOptions
            {
                PolitenessDelayMs = 0,
                EnableBrowserFallback = enableBrowser,
                RetryBlockedPagesWithBrowser = retryBlocked,
            },
        });

        return new PoliteHttpPageFetcher(
            new StubHttpClientFactory(handler),
            robots,
            options,
            NullLogger<PoliteHttpPageFetcher>.Instance,
            browser);
    }

    // ---- the NatWest case ------------------------------------------------

    [Fact]
    public async Task A_403_is_retried_with_the_browser_and_the_page_comes_back()
    {
        var browser = new StubBrowser { Text = "Colleague Technology Security Lead - London" };

        var result = await Build(
            new StubHttpHandler(403), browser, StubRobots.Allow()).FetchAsync(Url);

        Assert.True(result.Success);
        Assert.Equal("Playwright", result.Method);
        Assert.Contains("Technology Security Lead", result.Text);
        Assert.Equal(1, browser.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Every_refusal_status_earns_a_browser_retry(int status)
    {
        var browser = new StubBrowser { Text = "jobs here" };

        await Build(new StubHttpHandler(status), browser, StubRobots.Allow()).FetchAsync(Url);

        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task The_refusal_is_reported_faithfully_when_the_browser_cannot_help_either()
    {
        var browser = new StubBrowser { Succeeds = false };

        var result = await Build(
            new StubHttpHandler(403), browser, StubRobots.Allow()).FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.True(result.BlockedByBotProtection);
    }

    // ---- robots.txt stays the authority ---------------------------------

    [Fact]
    public async Task A_path_robots_txt_disallows_is_never_retried_with_the_browser()
    {
        // The whole point: the browser changes which client fetches, not what may be fetched.
        var browser = new StubBrowser { Text = "should never be reached" };

        var result = await Build(
            new StubHttpHandler(403), browser, StubRobots.Disallow()).FetchAsync(Url);

        Assert.False(result.Success);
        Assert.True(result.BlockedByRobots);
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task The_retry_can_be_turned_off()
    {
        var browser = new StubBrowser { Text = "jobs" };

        var result = await Build(
            new StubHttpHandler(403), browser, StubRobots.Allow(), retryBlocked: false).FetchAsync(Url);

        Assert.False(result.Success);
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task Nothing_is_retried_when_the_browser_is_disabled_altogether()
    {
        var browser = new StubBrowser { Text = "jobs" };

        await Build(new StubHttpHandler(403), browser, StubRobots.Allow(), enableBrowser: false)
            .FetchAsync(Url);

        Assert.Equal(0, browser.Calls);
    }

    // ---- statuses that are not a refusal --------------------------------

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(500)]
    public async Task An_ordinary_error_is_not_worth_a_browser_retry(int status)
    {
        // A missing page stays missing however it is fetched.
        var browser = new StubBrowser { Text = "jobs" };

        var result = await Build(new StubHttpHandler(status), browser, StubRobots.Allow()).FetchAsync(Url);

        Assert.False(result.Success);
        Assert.False(result.BlockedByBotProtection);
        Assert.Equal(0, browser.Calls);
    }

    // ---- stubs -----------------------------------------------------------

    private sealed class StubRobots(bool allowed, TimeSpan? crawlDelay = null) : IRobotsGate
    {
        public static StubRobots Allow() => new(true);
        public static StubRobots Disallow() => new(false);

        public Task<RobotsPolicy> GetPolicyAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(new RobotsPolicy(allowed, crawlDelay, WasRead: true));
    }

    private sealed class StubBrowser : IBrowserPageFetcher
    {
        public string Text { get; set; } = "";
        public bool Succeeds { get; set; } = true;
        public int Calls { get; private set; }

        public Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult(Succeeds
                ? new PageFetchResult
                {
                    Url = url,
                    Success = true,
                    Html = $"<html><body>{Text}</body></html>",
                    Text = Text,
                    Method = "Playwright",
                    StatusCode = 200,
                }
                : PageFetchResult.Failed(url, "browser blocked too", "Playwright"));
        }
    }

    private sealed class StubHttpHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent("blocked"),
            });
    }

    private sealed class StubHttpClientFactory(StubHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

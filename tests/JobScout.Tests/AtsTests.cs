using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Ats;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using JobScout.Web.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>Finding a company's job-board service from its URL, page or name.</summary>
public class AtsLocatorTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/monzo", AtsKind.Greenhouse, "monzo")]
    [InlineData("https://job-boards.greenhouse.io/monzo/jobs/8143930", AtsKind.Greenhouse, "monzo")]
    [InlineData("https://boards.greenhouse.io/embed/job_board?for=transferwise&b=https://wise.com", AtsKind.Greenhouse, "transferwise")]
    [InlineData("https://boards-api.greenhouse.io/v1/boards/dojo/jobs?content=true", AtsKind.Greenhouse, "dojo")]
    [InlineData("https://jobs.lever.co/scottlogic/46628ab4-a3ec", AtsKind.Lever, "scottlogic")]
    [InlineData("https://api.lever.co/v0/postings/quantcast?mode=json", AtsKind.Lever, "quantcast")]
    [InlineData("https://jobs.ashbyhq.com/paddle", AtsKind.Ashby, "paddle")]
    [InlineData("https://jobs.ashbyhq.com/clearbank/89dda110/application", AtsKind.Ashby, "clearbank")]
    [InlineData("https://api.ashbyhq.com/posting-api/job-board/krakentech", AtsKind.Ashby, "krakentech")]
    [InlineData("https://apply.workable.com/starling-bank/", AtsKind.Workable, "starling-bank")]
    [InlineData("https://apply.workable.com/starling-bank/j/0DA49B0B28/", AtsKind.Workable, "starling-bank")]
    [InlineData("https://apply.workable.com/api/v1/widget/accounts/cleo-ai", AtsKind.Workable, "cleo-ai")]
    [InlineData("//boards.greenhouse.io/embed/job_board/js?for=deliveroo", AtsKind.Greenhouse, "deliveroo")]
    [InlineData("jobs.lever.co/acme", AtsKind.Lever, "acme")]
    public void A_board_url_names_the_service_and_token(string url, AtsKind kind, string token) =>
        Assert.Equal(new AtsReference(kind, token), AtsLocator.TryParseUrl(url));

    [Theory]
    [InlineData("https://monzo.com/careers")]
    [InlineData("https://www.greenhouse.io/")]
    [InlineData("https://apply.workable.com/j/0DA49B0B28")] // an advert shortlink, not a board
    [InlineData("https://boards.greenhouse.io/embed/job_app?token=123")] // no board named
    [InlineData("https://job-boards.eu.greenhouse.io/acme")] // EU data centre, different API
    [InlineData("https://resources.workable.com/hiring")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_board(string? url) =>
        Assert.Null(AtsLocator.TryParseUrl(url));

    [Fact]
    public void A_careers_page_gives_its_board_away_through_embeds_and_links()
    {
        const string html = """
            <html><body>
              <script src="https://boards.greenhouse.io/embed/job_board/js?for=monzo&amp;b=x"></script>
              <a href="https://job-boards.greenhouse.io/monzo/jobs/1">Engineer</a>
              <a href="https://job-boards.greenhouse.io/monzo/jobs/2">Analyst</a>
              <a href="https://jobs.lever.co/other-co">Partner</a>
              <a href="https://www.greenhouse.io/">Powered by Greenhouse</a>
            </body></html>
            """;

        var found = AtsLocator.FindInHtml(html);

        Assert.Equal(
            [new AtsReference(AtsKind.Greenhouse, "monzo"), new AtsReference(AtsKind.Lever, "other-co")],
            found);
    }

    [Fact]
    public void A_page_with_no_board_finds_nothing() =>
        Assert.Empty(AtsLocator.FindInHtml("<html><body><a href='/jobs/1'>Engineer</a></body></html>"));

    [Theory]
    [InlineData("Funding Circle Ltd", new[] { "fundingcircle", "funding-circle" })]
    [InlineData("Monzo", new[] { "monzo" })]
    [InlineData("Ocado Technology plc", new[] { "ocadotechnology", "ocado-technology" })]
    [InlineData("AB", new string[0])]
    [InlineData("", new string[0])]
    public void Tokens_are_guessed_from_the_name(string name, string[] expected) =>
        Assert.Equal(expected, AtsLocator.GuessTokens(name));
}

/// <summary>Each feed's parser, run against a trimmed copy of a real response.</summary>
public class AtsFeedParsingTests
{
    private static readonly IOptions<JobScoutOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new JobScoutOptions());

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ats", name));

    [Fact]
    public void Greenhouse_adverts_are_read_with_their_description_unescaped()
    {
        var feed = new GreenhouseFeed(null!, Options, NullLogger<GreenhouseFeed>.Instance);

        var jobs = feed.Parse(Fixture("greenhouse.json"));

        Assert.Equal(2, jobs.Count);

        var job = jobs[0];
        Assert.Equal("Anaplan Support Analyst", job.Title);
        Assert.Equal("https://job-boards.greenhouse.io/monzo/jobs/8143930", job.Url);
        Assert.Contains("London", job.Location);
        Assert.True(job.IsRemote); // "Cardiff, London or Remote (UK)"
        Assert.NotNull(job.PostedAt);

        // Real text, with neither the escaped nor the raw markup left in it.
        Assert.False(string.IsNullOrWhiteSpace(job.Description));
        Assert.DoesNotContain("&lt;", job.Description);
        Assert.DoesNotContain("<p>", job.Description);
    }

    [Fact]
    public void Lever_postings_stitch_their_description_back_together()
    {
        var feed = new LeverFeed(null!, Options, NullLogger<LeverFeed>.Instance);

        var jobs = feed.Parse(Fixture("lever.json"));

        Assert.Equal(2, jobs.Count);

        var first = jobs[0];
        Assert.Equal("Data Engineer", first.Title);
        Assert.StartsWith("https://jobs.lever.co/scottlogic/", first.Url);
        Assert.Equal("Bristol", first.Location);
        Assert.False(first.IsRemote); // hybrid
        Assert.Contains("What are we looking for?", first.Description); // from the lists
        Assert.Null(first.SalaryMin);
        Assert.Equal(2026, first.PostedAt!.Value.Year);

        var second = jobs[1];
        Assert.True(second.IsRemote);
        Assert.Equal(55000m, second.SalaryMin);
        Assert.Equal(70000m, second.SalaryMax);
        Assert.Equal("GBP", second.SalaryCurrency);
    }

    [Fact]
    public void Ashby_skips_unlisted_postings_and_zero_salaries()
    {
        var feed = new AshbyFeed(null!, Options, NullLogger<AshbyFeed>.Instance);

        var jobs = feed.Parse(Fixture("ashby.json"));

        // Four in the file; the third is unlisted.
        Assert.Equal(3, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.Title.Contains("Customer and Regulatory"));

        var first = jobs[0];
        Assert.Equal("London Office - Hybrid", first.Location);
        Assert.False(first.IsRemote);
        Assert.False(string.IsNullOrWhiteSpace(first.Description));

        var salaried = jobs[1];
        Assert.Equal(60000m, salaried.SalaryMin);
        Assert.Equal(75000m, salaried.SalaryMax);
        Assert.Equal("GBP", salaried.SalaryCurrency);

        // Paddle publishes "not shown" as a zero range: that is no salary, not a £0 one.
        var remote = jobs[2];
        Assert.True(remote.IsRemote);
        Assert.Null(remote.SalaryMin);
        Assert.Null(remote.SalaryMax);
        Assert.Null(remote.SalaryCurrency);
        Assert.Contains("Portugal", remote.Location); // secondary locations are kept
    }

    [Fact]
    public void Workable_folds_one_advert_per_office_back_into_one()
    {
        var feed = new WorkableFeed(null!, Options, NullLogger<WorkableFeed>.Instance);

        var jobs = feed.Parse(Fixture("workable.json"));

        // The file lists the same advert twice, once for Manchester and once for Cardiff.
        var job = Assert.Single(jobs);
        Assert.Equal("Android Engineer", job.Title);
        Assert.Equal("https://apply.workable.com/j/0DA49B0B28", job.Url);
        Assert.Contains("Manchester", job.Location);
        Assert.Contains("Cardiff", job.Location);
        Assert.False(string.IsNullOrWhiteSpace(job.Description));
        Assert.DoesNotContain("<p>", job.Description);
        Assert.Equal(new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero), job.PostedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"jobs": []}""")]
    public void An_empty_board_is_an_empty_list(string json)
    {
        Assert.Empty(new GreenhouseFeed(null!, Options, NullLogger<GreenhouseFeed>.Instance).Parse(json));
        Assert.Empty(new AshbyFeed(null!, Options, NullLogger<AshbyFeed>.Instance).Parse(json));
        Assert.Empty(new WorkableFeed(null!, Options, NullLogger<WorkableFeed>.Instance).Parse(json));
    }

    [Fact]
    public void Feed_urls_have_the_expected_shape()
    {
        Assert.Equal("https://boards-api.greenhouse.io/v1/boards/monzo/jobs?content=true",
            new GreenhouseFeed(null!, Options, NullLogger<GreenhouseFeed>.Instance).BuildUrl("monzo"));
        Assert.Equal("https://api.lever.co/v0/postings/scottlogic?mode=json",
            new LeverFeed(null!, Options, NullLogger<LeverFeed>.Instance).BuildUrl("scottlogic"));
        Assert.Equal("https://api.ashbyhq.com/posting-api/job-board/paddle?includeCompensation=true",
            new AshbyFeed(null!, Options, NullLogger<AshbyFeed>.Instance).BuildUrl("paddle"));
        Assert.Equal("https://apply.workable.com/api/v1/widget/accounts/starling-bank?details=true",
            new WorkableFeed(null!, Options, NullLogger<WorkableFeed>.Instance).BuildUrl("starling-bank"));
    }
}

/// <summary>Detection, and the morning scan reading feeds instead of careers pages.</summary>
public class AtsScanTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeJobMatcher _matcher = new();
    private readonly FakePageFetcher _fetcher = new();
    private readonly FakeAtsFeed _greenhouse = new(AtsKind.Greenhouse);
    private readonly FakeAtsFeed _lever = new(AtsKind.Lever);
    private readonly AtsOptions _atsOptions = new();
    private int _extractCalls;

    public AtsScanTests()
    {
        _matcher.ExtractHandler = (_, _, _) =>
        {
            _extractCalls++;
            return [new ExtractedJob { Title = "From the page", Url = "https://example.test/careers/1", Location = "London" }];
        };
    }

    public void Dispose() => _db.Dispose();

    private IOptions<JobScoutOptions> Options => Microsoft.Extensions.Options.Options.Create(new JobScoutOptions
    {
        Scoring = new ScoringOptions { MaxListingsPerRun = 50, MaxConcurrency = 1 },
        Ats = _atsOptions,
    });

    private AtsService BuildAts() =>
        new(_db, [_greenhouse, _lever], Options, NullLogger<AtsService>.Instance);

    private MorningScanJob BuildScan() => new(
        _db,
        _fetcher,
        _matcher,
        new ListingUpsertService(_db, NullLogger<ListingUpsertService>.Instance),
        new ScoringService(_db, _matcher, _fetcher, Options, NullLogger<ScoringService>.Instance),
        BuildAts(),
        NullLogger<MorningScanJob>.Instance);

    private async Task<int> SeedCompanyAsync(
        string name, string? url = null, AtsKind kind = AtsKind.None, string? token = null,
        DateTimeOffset? atsCheckedAt = null)
    {
        await using var db = _db.CreateDbContext();

        if (!await db.AppConfigs.AnyAsync())
        {
            db.AppConfigs.Add(new AppConfig
            {
                Id = 1,
                Currency = "GBP",
                CvText = "Ten years of C# and distributed systems.",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var company = new Company
        {
            Name = name,
            NormalisedName = CompanyNameNormaliser.Normalise(name),
            Url = url,
            Source = "Manual",
            Status = CompanyStatus.USE,
            DiscoveredAt = DateTimeOffset.UtcNow,
            AtsKind = kind,
            AtsToken = token,
            AtsCheckedAt = atsCheckedAt,
        };

        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static ExtractedJob Advert(string title, int id) => new()
    {
        Title = title,
        Url = $"https://job-boards.greenhouse.io/acme/jobs/{id}",
        Location = "London",
        Description = "A full description, straight from the feed.",
    };

    private async Task<Company> CompanyAsync(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Companies.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<List<JobListing>> ListingsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.JobListings.AsNoTracking().ToListAsync();
    }

    // ---- detection ------------------------------------------------------

    [Fact]
    public async Task A_board_named_by_the_careers_url_counts_even_when_empty()
    {
        _greenhouse.Boards["acme"] = [];

        var found = await BuildAts().DetectAsync("Acme", "https://boards.greenhouse.io/acme");

        Assert.NotNull(found);
        Assert.Equal(AtsKind.Greenhouse, found!.Kind);
        Assert.False(found.Guessed);
    }

    [Fact]
    public async Task A_guessed_board_must_have_adverts_on_it()
    {
        // Someone owns "acme" on Greenhouse, but with nothing on it that is no evidence it is us.
        _greenhouse.Boards["acme"] = [];

        Assert.Null(await BuildAts().DetectAsync("Acme", null));

        _greenhouse.Boards["acme"] = [Advert("Engineer", 1)];

        var found = await BuildAts().DetectAsync("Acme", null);

        Assert.NotNull(found);
        Assert.True(found!.Guessed);
        Assert.Equal("acme", found.Token);
    }

    [Fact]
    public async Task Guessing_tries_every_service_and_both_token_shapes()
    {
        _lever.Boards["funding-circle"] = [Advert("Engineer", 1)];

        var found = await BuildAts().DetectAsync("Funding Circle Ltd", null);

        Assert.Equal(new AtsReference(AtsKind.Lever, "funding-circle"), new AtsReference(found!.Kind, found.Token));
        Assert.Contains("fundingcircle", _greenhouse.Requested);
        Assert.Contains("fundingcircle", _lever.Requested);
    }

    [Fact]
    public async Task A_saved_detection_never_overwrites_a_feed_set_by_hand()
    {
        var id = await SeedCompanyAsync("Acme", kind: AtsKind.Lever, token: "acme-manual");

        await BuildAts().SaveDetectionAsync(id, new AtsDetection(AtsKind.Greenhouse, "acme", true, []));

        var company = await CompanyAsync(id);
        Assert.Equal(AtsKind.Lever, company.AtsKind);
        Assert.Equal("acme-manual", company.AtsToken);
        Assert.NotNull(company.AtsCheckedAt);
    }

    // ---- the morning scan -----------------------------------------------

    [Fact]
    public async Task A_company_with_a_feed_is_read_from_it_not_from_its_careers_page()
    {
        await SeedCompanyAsync("Acme", "https://example.test/careers", AtsKind.Greenhouse, "acme");
        _fetcher.Pages["https://example.test/careers"] = "Careers page text.";
        _greenhouse.Boards["acme"] = [Advert("Backend Engineer", 1), Advert("Platform Engineer", 2)];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Empty(result.Issues);
        Assert.Contains("1/1 job-board feed(s) read", result.Summary);
        Assert.DoesNotContain("https://example.test/careers", _fetcher.Requested);
        Assert.Equal(0, _extractCalls);

        var listings = await ListingsAsync();
        Assert.Equal(2, listings.Count);
        Assert.All(listings, l => Assert.Equal("Greenhouse", l.Source));
        Assert.All(listings, l => Assert.Equal("A full description, straight from the feed.", l.Description));
    }

    [Fact]
    public async Task A_failed_feed_falls_back_to_the_careers_page_and_says_so()
    {
        await SeedCompanyAsync("Acme", "https://example.test/careers", AtsKind.Greenhouse, "acme");
        _fetcher.Pages["https://example.test/careers"] = "Careers page text.";
        _greenhouse.Fail = true;

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("job-board feed(s) could not be read") && i.Contains("Acme"));
        Assert.Contains("0/1 job-board feed(s) read", result.Summary);
        Assert.Contains("1/1 careers page(s) read", result.Summary);
        Assert.Equal(1, _extractCalls);

        var listing = Assert.Single(await ListingsAsync());
        Assert.Equal("CareerPage", listing.Source);
    }

    [Fact]
    public async Task A_failed_feed_with_no_careers_page_is_still_reported()
    {
        await SeedCompanyAsync("Acme", url: null, AtsKind.Greenhouse, "acme");
        _greenhouse.Fail = true;

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("Acme"));
        Assert.Empty(await ListingsAsync());
    }

    [Fact]
    public async Task A_board_linked_from_the_careers_page_is_adopted_and_saves_the_ai_call()
    {
        // Checked recently, so the pre-scan detection leaves it alone and only the page can
        // give the board away.
        var id = await SeedCompanyAsync("Acme", "https://example.test/careers", atsCheckedAt: DateTimeOffset.UtcNow);
        _fetcher.Pages["https://example.test/careers"] =
            "Open roles <a href=\"https://job-boards.greenhouse.io/acme-careers/jobs/1\">Engineer</a>";
        _greenhouse.Boards["acme-careers"] = [Advert("Backend Engineer", 1)];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Equal(0, _extractCalls);
        Assert.Contains("Acme on Greenhouse as 'acme-careers'", result.Summary);

        var company = await CompanyAsync(id);
        Assert.Equal(AtsKind.Greenhouse, company.AtsKind);
        Assert.Equal("acme-careers", company.AtsToken);

        var listing = Assert.Single(await ListingsAsync());
        Assert.Equal("Greenhouse", listing.Source);
    }

    [Fact]
    public async Task A_company_with_no_careers_page_can_be_found_by_its_name_before_the_scan()
    {
        var id = await SeedCompanyAsync("Acme");
        _lever.Boards["acme"] = [Advert("Backend Engineer", 1)];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains("guessed from the name", result.Summary);
        Assert.Contains("1/1 job-board feed(s) read", result.Summary);

        var company = await CompanyAsync(id);
        Assert.Equal(AtsKind.Lever, company.AtsKind);
        Assert.Single(await ListingsAsync());
    }

    [Fact]
    public async Task A_company_checked_recently_is_not_probed_again()
    {
        await SeedCompanyAsync("Acme", atsCheckedAt: DateTimeOffset.UtcNow.AddDays(-3));
        _lever.Boards["acme"] = [Advert("Backend Engineer", 1)];

        await BuildScan().RunAsync(CancellationToken.None);

        Assert.Empty(_lever.Requested);
        Assert.Empty(_greenhouse.Requested);
    }

    [Fact]
    public async Task A_company_checked_long_ago_is_probed_again()
    {
        await SeedCompanyAsync("Acme", atsCheckedAt: DateTimeOffset.UtcNow.AddDays(-30));
        _lever.Boards["acme"] = [Advert("Backend Engineer", 1)];

        await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains("acme", _lever.Requested);
    }

    [Fact]
    public async Task Detection_stops_at_the_per_run_cap()
    {
        _atsOptions.MaxDetectionsPerRun = 2;

        foreach (var name in new[] { "Alpha Co", "Bravo Co", "Charlie Co" })
            await SeedCompanyAsync(name);

        await BuildScan().RunAsync(CancellationToken.None);

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.Companies.CountAsync(c => c.AtsCheckedAt != null));
    }

    [Fact]
    public async Task Turning_feeds_off_reads_careers_pages_as_before()
    {
        _atsOptions.Enabled = false;

        await SeedCompanyAsync("Acme", "https://example.test/careers", AtsKind.Greenhouse, "acme");
        _fetcher.Pages["https://example.test/careers"] = "Careers page text.";
        _greenhouse.Boards["acme"] = [Advert("Backend Engineer", 1)];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Empty(_greenhouse.Requested);
        Assert.Equal(1, _extractCalls);
        Assert.DoesNotContain("job-board feed", result.Summary);
    }
}

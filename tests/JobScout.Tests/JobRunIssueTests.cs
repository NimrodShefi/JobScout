using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using JobScout.Web.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>A run that goes wrong must not come back looking clean.
///
/// This is the regression guard for the morning scan of 21 Sep 2026: every AI call returned
/// HTTP 400, every company reported "0 advert(s) found", and the dashboard showed a green run
/// reading "15/15 careers page(s) read" - indistinguishable from a morning with no new jobs.</summary>
public class JobRunIssueTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeJobMatcher _matcher = new();
    private readonly FakePageFetcher _fetcher = new();

    public void Dispose() => _db.Dispose();

    private MorningScanJob BuildScan()
    {
        var options = Options.Create(new JobScoutOptions
        {
            Scoring = new ScoringOptions { MaxListingsPerRun = 50, MaxConcurrency = 1 },
        });

        return new MorningScanJob(
            _db,
            _fetcher,
            _matcher,
            new ListingUpsertService(_db, NullLogger<ListingUpsertService>.Instance),
            new ScoringService(_db, _matcher, _fetcher, options, NullLogger<ScoringService>.Instance),
            NullLogger<MorningScanJob>.Instance);
    }

    private async Task SeedCompanyAsync(string name, string url)
    {
        await using var db = _db.CreateDbContext();

        db.Companies.Add(new Company
        {
            Name = name,
            NormalisedName = CompanyNameNormaliser.Normalise(name),
            Url = url,
            Source = "Manual",
            Status = CompanyStatus.USE,
            DiscoveredAt = DateTimeOffset.UtcNow,
        });

        db.AppConfigs.Add(new AppConfig
        {
            Id = 1,
            Currency = "GBP",
            CvText = "Ten years of C# and distributed systems.",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        _fetcher.Pages[url] = "Careers page text long enough to be worth reading.";
    }

    // ---- the morning scan ------------------------------------------------

    [Fact]
    public async Task A_failed_ai_extraction_is_reported_as_an_issue()
    {
        await SeedCompanyAsync("Bloomberg", "https://example.test/careers");

        // Null is what the matcher returns when the provider rejected every attempt.
        _matcher.ExtractHandler = (_, _, _) => null;

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.NotEmpty(result.Issues);
        Assert.Contains(result.Issues, i => i.Contains("AI", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Bloomberg", string.Join(" ", result.Issues));
    }

    [Fact]
    public async Task A_failed_ai_extraction_does_not_count_the_page_as_read()
    {
        await SeedCompanyAsync("Bloomberg", "https://example.test/careers");
        _matcher.ExtractHandler = (_, _, _) => null;

        var result = await BuildScan().RunAsync(CancellationToken.None);

        // The old summary said "1/1 careers page(s) read", which was technically true and
        // entirely misleading - nothing was extracted from it.
        Assert.DoesNotContain("1/1 careers page(s) read", result.Summary);
        Assert.Contains("not understood by the AI", result.Summary);
    }

    [Fact]
    public async Task A_page_with_no_adverts_is_not_an_issue()
    {
        await SeedCompanyAsync("Bloomberg", "https://example.test/careers");

        // Empty list, not null: the AI answered, the page simply had nothing on it.
        _matcher.ExtractHandler = (_, _, _) => [];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Empty(result.Issues);
        Assert.Contains("1/1 careers page(s) read", result.Summary);
    }

    [Fact]
    public async Task An_unreachable_careers_page_is_reported_as_an_issue()
    {
        await SeedCompanyAsync("Bloomberg", "https://example.test/careers");
        _fetcher.Pages.Clear();

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("could not be downloaded"));
    }

    [Fact]
    public async Task A_missing_cv_is_reported_as_an_issue()
    {
        await SeedCompanyAsync("Bloomberg", "https://example.test/careers");

        await using (var db = _db.CreateDbContext())
        {
            var config = db.AppConfigs.Single();
            config.CvText = null;
            await db.SaveChangesAsync();
        }

        _matcher.ExtractHandler = (_, url, _) =>
            [new ExtractedJob { Title = "Staff Engineer", Url = url + "/1", Location = "London" }];

        var result = await BuildScan().RunAsync(CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("No CV"));
    }

    // ---- discovery -------------------------------------------------------

    [Fact]
    public async Task A_failed_board_query_is_reported_as_an_issue()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Industries.Add(new Industry { Name = "Software", IsActive = true });
            db.AppConfigs.Add(new AppConfig { Id = 1, Currency = "GBP", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var board = new FakeJobBoardProvider { FailQueries = true };

        var job = new DiscoveryJob(
            _db, [board],
            new ListingUpsertService(_db, NullLogger<ListingUpsertService>.Instance),
            Options.Create(new JobScoutOptions()),
            NullLogger<DiscoveryJob>.Instance);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("failed"));
        Assert.Contains("FakeBoard", string.Join(" ", result.Issues));
    }

    [Fact]
    public async Task Discovery_stops_scanning_once_the_new_company_cap_is_reached()
    {
        await using (var db = _db.CreateDbContext())
        {
            // Ten industries would be ten queries if nothing stopped the run.
            foreach (var n in Enumerable.Range(1, 10))
                db.Industries.Add(new Industry { Name = $"Industry{n}", IsActive = true });

            db.AppConfigs.Add(new AppConfig { Id = 1, Currency = "GBP", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var board = new FakeJobBoardProvider();

        // One query alone carries more unseen companies than the cap allows.
        foreach (var n in Enumerable.Range(1, 8))
        {
            board.Results.Add(new BoardJobResult
            {
                CompanyName = $"Company{n}",
                Title = "Engineer",
                Url = $"https://board.test/{n}",
            });
        }

        var options = Options.Create(new JobScoutOptions
        {
            Discovery = new DiscoveryOptions { MaxNewCompaniesPerRun = 5 },
        });

        var job = new DiscoveryJob(
            _db, [board],
            new ListingUpsertService(_db, NullLogger<ListingUpsertService>.Instance),
            options,
            NullLogger<DiscoveryJob>.Instance);

        var result = await job.RunAsync(CancellationToken.None);

        await using var check = _db.CreateDbContext();
        Assert.Equal(5, await check.Companies.CountAsync());

        // The point of the cap: the remaining nine industries are never queried.
        Assert.Single(board.Requests);
        Assert.Contains("cap reached", result.Summary);
    }

    // ---- what the dashboard shows ---------------------------------------

    [Fact]
    public void A_finished_run_carrying_issues_is_not_reported_as_ok()
    {
        var run = new JobRunLog
        {
            JobName = "morning-scan",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
            Success = true,
            Summary = "15/15 careers page(s) read; 0 new listing(s)",
            Issues = "The AI could not read adverts from all 15 careers page(s).",
        };

        Assert.Equal(JobRunOutcome.CompletedWithIssues, run.Outcome);
        Assert.True(run.HasIssues);
    }

    [Theory]
    [InlineData(true, null, JobRunOutcome.Ok)]
    [InlineData(false, null, JobRunOutcome.Failed)]
    [InlineData(false, "something went wrong", JobRunOutcome.Failed)]
    public void Outcome_reflects_success_and_issues(bool success, string? issues, JobRunOutcome expected)
    {
        var run = new JobRunLog
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
            Success = success,
            Issues = issues,
        };

        Assert.Equal(expected, run.Outcome);
    }

    [Fact]
    public void A_run_still_in_flight_is_running_whatever_else_is_set()
    {
        var run = new JobRunLog { StartedAt = DateTimeOffset.UtcNow, FinishedAt = null };

        Assert.Equal(JobRunOutcome.Running, run.Outcome);
    }
}

using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>Scoring only touches USE companies, skips unchanged listings, and never makes
/// more AI calls than the configured cap.</summary>
public class ScoringTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeJobMatcher _matcher = new();
    private readonly FakePageFetcher _fetcher = new();

    public void Dispose() => _db.Dispose();

    private ScoringService BuildService(int cap = 50)
    {
        var options = Options.Create(new JobScoutOptions
        {
            Scoring = new ScoringOptions { MaxListingsPerRun = cap, MaxConcurrency = 1 },
        });

        return new ScoringService(
            _db, _matcher, _fetcher, options, NullLogger<ScoringService>.Instance);
    }

    private async Task SeedCvAsync()
    {
        await using var db = _db.CreateDbContext();

        var config = await db.AppConfigs.FirstOrDefaultAsync();
        if (config is null)
        {
            config = new AppConfig { Id = 1, Currency = "GBP" };
            db.AppConfigs.Add(config);
        }

        config.CvText = "Ten years of C# and distributed systems.";
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
    }

    private async Task<int> SeedCompanyAsync(string name, CompanyStatus status)
    {
        await using var db = _db.CreateDbContext();

        var company = new Company
        {
            Name = name,
            NormalisedName = CompanyNameNormaliser.Normalise(name),
            Status = status,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };

        db.Companies.Add(company);
        await db.SaveChangesAsync();

        return company.Id;
    }

    private async Task<int> SeedListingAsync(
        int companyId, string title, string? description = "A long description. " + LongText)
    {
        await using var db = _db.CreateDbContext();

        var listing = new JobListing
        {
            CompanyId = companyId,
            Title = title,
            Url = $"https://test/{Guid.NewGuid():N}",
            Description = description,
            Source = "CareerPage",
            FoundAt = DateTimeOffset.UtcNow,
            Status = JobListingStatus.New,
        };

        db.JobListings.Add(listing);
        await db.SaveChangesAsync();

        return listing.Id;
    }

    /// <summary>Long enough that the scorer does not decide it needs to fetch a fuller one.</summary>
    private const string LongText =
        "We are looking for an engineer with production experience across services, storage and " +
        "delivery. You will design, build and operate systems used by a large customer base, and " +
        "work closely with product and design. Experience with C#, distributed systems and cloud " +
        "infrastructure is valued. This is a permanent role with hybrid working arrangements and " +
        "a structured progression framework, mentoring and a generous learning budget.";

    // ---- scope ----------------------------------------------------------

    [Fact]
    public async Task Only_listings_belonging_to_USE_companies_are_scored()
    {
        await SeedCvAsync();

        var use = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var review = await SeedCompanyAsync("Globex", CompanyStatus.REVIEW);
        var notUse = await SeedCompanyAsync("Initech", CompanyStatus.NOT_USE);

        await SeedListingAsync(use, "Scored");
        await SeedListingAsync(review, "Not scored - REVIEW");
        await SeedListingAsync(notUse, "Not scored - NOT_USE");

        var outcome = await BuildService().ScorePendingAsync();

        Assert.Equal(1, outcome.Scored);
        Assert.Equal(["Scored"], _matcher.ScoredTitles);
    }

    [Fact]
    public async Task Moving_a_company_to_USE_lets_its_board_listings_be_scored()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Globex", CompanyStatus.REVIEW);
        await SeedListingAsync(companyId, "Discovered from a board");

        Assert.Equal(0, (await BuildService().ScorePendingAsync()).Scored);

        await using (var db = _db.CreateDbContext())
        {
            (await db.Companies.FindAsync(companyId))!.Status = CompanyStatus.USE;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await BuildService().ScorePendingAsync(companyId)).Scored);
    }

    [Fact]
    public async Task Scoring_one_company_leaves_the_others_alone()
    {
        await SeedCvAsync();

        var acme = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var globex = await SeedCompanyAsync("Globex", CompanyStatus.USE);

        await SeedListingAsync(acme, "Acme role");
        await SeedListingAsync(globex, "Globex role");

        await BuildService().ScorePendingAsync(globex);

        Assert.Equal(["Globex role"], _matcher.ScoredTitles);
    }

    [Fact]
    public async Task With_no_CV_nothing_is_scored_and_no_AI_call_is_made()
    {
        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        await SeedListingAsync(companyId, "Engineer");

        var outcome = await BuildService().ScorePendingAsync();

        Assert.Equal(0, outcome.Scored);
        Assert.Equal(0, _matcher.ScoreCalls);
    }

    // ---- results --------------------------------------------------------

    [Fact]
    public async Task A_score_is_saved_with_its_reasoning_strengths_and_gaps()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var listingId = await SeedListingAsync(companyId, "Engineer");

        _matcher.ScoreHandler = (_, _, _) => new JobMatchResult
        {
            Score = 88,
            Reasoning = "Strong overlap on the core stack.",
            Strengths = ["C#", "Distributed systems"],
            Gaps = ["No Kubernetes"],
            SalaryMin = 80_000,
            SalaryMax = 95_000,
            SalaryCurrency = "GBP",
            Location = "London",
            IsRemote = true,
        };

        await BuildService().ScorePendingAsync();

        await using var db = _db.CreateDbContext();
        var listing = await db.JobListings.FindAsync(listingId);

        Assert.Equal(88, listing!.Score);
        Assert.Equal(JobListingStatus.Scored, listing.Status);
        Assert.Equal("Strong overlap on the core stack.", listing.Reasoning);
        Assert.Equal("C#\nDistributed systems", listing.Strengths);
        Assert.Equal("No Kubernetes", listing.Gaps);
        Assert.NotNull(listing.ScoredAt);
        Assert.NotNull(listing.ScoredContentHash);
    }

    [Fact]
    public async Task A_salary_the_AI_reads_off_the_advert_is_recorded()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var listingId = await SeedListingAsync(companyId, "Engineer");

        _matcher.ScoreHandler = (_, _, _) => new JobMatchResult
        {
            Score = 70,
            SalaryMin = 75_000,
            SalaryMax = 85_000,
            SalaryCurrency = "GBP",
        };

        await BuildService().ScorePendingAsync();

        await using var db = _db.CreateDbContext();
        var listing = await db.JobListings.FindAsync(listingId);

        Assert.True(listing!.SalaryKnown);
        Assert.Equal(85_000, listing.SalaryMax);
    }

    [Fact]
    public async Task A_listing_I_dismissed_keeps_that_status_even_after_being_scored()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var listingId = await SeedListingAsync(companyId, "Engineer");

        await using (var db = _db.CreateDbContext())
        {
            (await db.JobListings.FindAsync(listingId))!.Status = JobListingStatus.Dismissed;
            await db.SaveChangesAsync();
        }

        await BuildService().ScorePendingAsync();

        await using var check = _db.CreateDbContext();
        var listing = await check.JobListings.FindAsync(listingId);

        Assert.Equal(JobListingStatus.Dismissed, listing!.Status);
        Assert.NotNull(listing.Score);
    }

    [Fact]
    public async Task A_matcher_that_gives_up_is_counted_as_a_failure()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        await SeedListingAsync(companyId, "Engineer");

        _matcher.ScoreHandler = (_, _, _) => null;

        var outcome = await BuildService().ScorePendingAsync();

        Assert.Equal(0, outcome.Scored);
        Assert.Equal(1, outcome.Failed);
    }

    // ---- cost control ---------------------------------------------------

    [Fact]
    public async Task The_per_run_cap_limits_how_many_AI_calls_are_made()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        for (var i = 0; i < 10; i++)
            await SeedListingAsync(companyId, $"Role {i}");

        var outcome = await BuildService(cap: 4).ScorePendingAsync();

        Assert.Equal(4, outcome.Scored);
        Assert.Equal(4, _matcher.ScoreCalls);
        Assert.True(outcome.HitCap);
    }

    [Fact]
    public async Task The_rest_are_picked_up_by_the_following_run()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        for (var i = 0; i < 6; i++)
            await SeedListingAsync(companyId, $"Role {i}");

        await BuildService(cap: 4).ScorePendingAsync();
        var second = await BuildService(cap: 4).ScorePendingAsync();

        Assert.Equal(2, second.Scored);
        Assert.False(second.HitCap);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.JobListings.CountAsync(l => l.Score == null));
    }

    [Fact]
    public async Task An_unchanged_listing_is_not_scored_again()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var listingId = await SeedListingAsync(companyId, "Engineer");

        await BuildService().ScorePendingAsync();
        Assert.Equal(1, _matcher.ScoreCalls);

        // Put it back in the queue without changing the content.
        await using (var db = _db.CreateDbContext())
        {
            (await db.JobListings.FindAsync(listingId))!.Status = JobListingStatus.New;
            await db.SaveChangesAsync();
        }

        var second = await BuildService().ScorePendingAsync();

        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Scored);
        Assert.Equal(1, _matcher.ScoreCalls); // still just the one call
    }

    [Fact]
    public async Task A_rewritten_advert_is_scored_again()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        var listingId = await SeedListingAsync(companyId, "Engineer");

        await BuildService().ScorePendingAsync();

        await using (var db = _db.CreateDbContext())
        {
            var listing = await db.JobListings.FindAsync(listingId);
            listing!.Description = "Completely rewritten advert. " + LongText;
            listing.Status = JobListingStatus.New;
            await db.SaveChangesAsync();
        }

        var second = await BuildService().ScorePendingAsync();

        Assert.Equal(1, second.Scored);
        Assert.Equal(2, _matcher.ScoreCalls);
    }

    [Fact]
    public void The_content_hash_tracks_the_text_that_was_actually_judged()
    {
        var a = ScoringService.ContentHash("Engineer", "London", "Description");
        var b = ScoringService.ContentHash("Engineer", "London", "Description");
        var c = ScoringService.ContentHash("Engineer", "Bristol", "Description");
        var d = ScoringService.ContentHash("Engineer", "London", "Different");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    // ---- thin descriptions ----------------------------------------------

    [Fact]
    public async Task A_thin_board_description_is_fetched_from_the_advert_page_first()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Globex", CompanyStatus.USE);

        int listingId;
        string url;

        await using (var db = _db.CreateDbContext())
        {
            var listing = new JobListing
            {
                CompanyId = companyId,
                Title = "Engineer",
                Url = "https://board.test/ad/1",
                Description = "Short teaser.",
                Source = "Adzuna",
                FoundAt = DateTimeOffset.UtcNow,
            };
            db.JobListings.Add(listing);
            await db.SaveChangesAsync();

            listingId = listing.Id;
            url = listing.Url;
        }

        _fetcher.Pages[url] = "The full advert text. " + LongText;

        string? seenDescription = null;
        _matcher.ScoreHandler = (_, _, description) =>
        {
            seenDescription = description;
            return new JobMatchResult { Score = 65 };
        };

        var outcome = await BuildService().ScorePendingAsync();

        Assert.Equal(1, outcome.DescriptionsFetched);
        Assert.Contains(url, _fetcher.Requested);
        Assert.Contains("The full advert text.", seenDescription);

        // The fuller text is kept, so the next run does not fetch it again.
        await using var check = _db.CreateDbContext();
        Assert.Contains("The full advert text.", (await check.JobListings.FindAsync(listingId))!.Description);
    }

    [Fact]
    public async Task A_listing_that_already_has_a_full_description_is_not_re_fetched()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Acme", CompanyStatus.USE);
        await SeedListingAsync(companyId, "Engineer");

        await BuildService().ScorePendingAsync();

        Assert.Empty(_fetcher.Requested);
    }

    [Fact]
    public async Task A_failed_description_fetch_still_lets_the_listing_be_scored()
    {
        await SeedCvAsync();

        var companyId = await SeedCompanyAsync("Globex", CompanyStatus.USE);
        await SeedListingAsync(companyId, "Engineer", description: "Tiny.");

        var outcome = await BuildService().ScorePendingAsync();

        Assert.Equal(0, outcome.DescriptionsFetched);
        Assert.Equal(1, outcome.Scored);
    }
}

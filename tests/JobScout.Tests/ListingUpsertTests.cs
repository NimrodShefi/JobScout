using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Services;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobScout.Tests;

/// <summary>Dedupe by URL and the board-discovery company upsert.</summary>
public class ListingUpsertTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ListingUpsertService _service;

    public ListingUpsertTests() =>
        _service = new ListingUpsertService(_db, NullLogger<ListingUpsertService>.Instance);

    public void Dispose() => _db.Dispose();

    private static MatchCriteria AnyLocationAnySalary => new()
    {
        MinimumSalary = 0,
        Currency = "GBP",
        Cities = [],
        IncludeRemote = true,
    };

    private async Task<int> AddCompanyAsync(string name, CompanyStatus status = CompanyStatus.USE)
    {
        await using var db = _db.CreateDbContext();

        var company = new Company
        {
            Name = name,
            NormalisedName = CompanyNameNormaliser.Normalise(name),
            Source = "Manual",
            Status = status,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };

        db.Companies.Add(company);
        await db.SaveChangesAsync();

        return company.Id;
    }

    private static ExtractedJob Job(string title, string url, string? location = "London") => new()
    {
        Title = title,
        Url = url,
        Location = location,
        Description = "A description long enough to be useful.",
    };

    // ---- dedupe ---------------------------------------------------------

    [Fact]
    public async Task The_same_url_twice_in_one_batch_is_inserted_once()
    {
        var companyId = await AddCompanyAsync("Acme");

        var outcome = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            Job("Engineer", "https://acme.test/jobs/1"),
            Job("Engineer", "https://acme.test/jobs/1"),
        ], AnyLocationAnySalary);

        Assert.Equal(1, outcome.Inserted);
        Assert.Equal(1, outcome.Duplicates);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.JobListings.CountAsync());
    }

    [Fact]
    public async Task Re_running_a_scan_does_not_insert_the_listing_again()
    {
        var companyId = await AddCompanyAsync("Acme");
        var jobs = new[] { Job("Engineer", "https://acme.test/jobs/1") };

        await _service.UpsertForCompanyAsync(companyId, "CareerPage", jobs, AnyLocationAnySalary);
        var second = await _service.UpsertForCompanyAsync(companyId, "CareerPage", jobs, AnyLocationAnySalary);

        Assert.Equal(0, second.Inserted);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.JobListings.CountAsync());
    }

    [Theory]
    // Fragment, trailing slash, host case and default port all describe the same advert.
    [InlineData("https://acme.test/jobs/1", "https://acme.test/jobs/1#apply")]
    [InlineData("https://acme.test/jobs/1", "https://acme.test/jobs/1/")]
    [InlineData("https://acme.test/jobs/1", "https://ACME.test/jobs/1")]
    [InlineData("https://acme.test/jobs/1", "https://acme.test:443/jobs/1")]
    public async Task Urls_that_differ_only_cosmetically_are_one_listing(string first, string second)
    {
        var companyId = await AddCompanyAsync("Acme");

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
            [Job("Engineer", first)], AnyLocationAnySalary);

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
            [Job("Engineer", second)], AnyLocationAnySalary);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.JobListings.CountAsync());
    }

    [Fact]
    public async Task A_query_string_still_distinguishes_two_adverts()
    {
        // Many boards put the advert id in the query, so it must not be stripped.
        var companyId = await AddCompanyAsync("Acme");

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            Job("Engineer", "https://acme.test/jobs?id=1"),
            Job("Designer", "https://acme.test/jobs?id=2"),
        ], AnyLocationAnySalary);

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.JobListings.CountAsync());
    }

    [Fact]
    public async Task A_second_pass_fills_in_details_that_were_missing()
    {
        var companyId = await AddCompanyAsync("Acme");

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            new ExtractedJob { Title = "Engineer", Url = "https://acme.test/jobs/1", Location = null },
        ], AnyLocationAnySalary);

        var second = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            new ExtractedJob
            {
                Title = "Engineer",
                Url = "https://acme.test/jobs/1",
                Location = "London",
                Description = "Now with a description.",
                SalaryMin = 70_000,
                SalaryMax = 90_000,
                SalaryCurrency = "GBP",
            },
        ], AnyLocationAnySalary);

        Assert.Equal(1, second.Updated);

        await using var db = _db.CreateDbContext();
        var listing = await db.JobListings.SingleAsync();

        Assert.Equal("London", listing.Location);
        Assert.Equal("Now with a description.", listing.Description);
        Assert.True(listing.SalaryKnown);
        Assert.Equal(90_000, listing.SalaryMax);
    }

    [Fact]
    public async Task A_listing_I_dismissed_is_not_resurrected_by_a_later_scan()
    {
        var companyId = await AddCompanyAsync("Acme");

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
            [Job("Engineer", "https://acme.test/jobs/1")], AnyLocationAnySalary);

        await using (var db = _db.CreateDbContext())
        {
            var listing = await db.JobListings.SingleAsync();
            listing.Status = JobListingStatus.Dismissed;
            await db.SaveChangesAsync();
        }

        await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            new ExtractedJob
            {
                Title = "Engineer",
                Url = "https://acme.test/jobs/1",
                Description = "Rewritten advert.",
            },
        ], AnyLocationAnySalary);

        await using var check = _db.CreateDbContext();
        var after = await check.JobListings.SingleAsync();

        Assert.Equal(JobListingStatus.Dismissed, after.Status);
    }

    // ---- filtering on the way in ---------------------------------------

    [Fact]
    public async Task Listings_outside_my_cities_are_not_saved()
    {
        var companyId = await AddCompanyAsync("Acme");

        var criteria = new MatchCriteria
        {
            MinimumSalary = 0,
            Cities = ["London"],
            IncludeRemote = false,
        };

        var outcome = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            Job("Kept", "https://acme.test/jobs/1", "London"),
            Job("Dropped", "https://acme.test/jobs/2", "Bristol"),
        ], criteria);

        Assert.Equal(1, outcome.Inserted);
        Assert.Equal(1, outcome.RejectedLocation);

        await using var db = _db.CreateDbContext();
        Assert.Equal("Kept", (await db.JobListings.SingleAsync()).Title);
    }

    [Fact]
    public async Task Listings_below_my_minimum_salary_are_not_saved_but_unknown_ones_are()
    {
        var companyId = await AddCompanyAsync("Acme");

        var criteria = new MatchCriteria { MinimumSalary = 70_000, Cities = [], IncludeRemote = true };

        var outcome = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            new ExtractedJob { Title = "Too low", Url = "https://acme.test/1", SalaryMax = 40_000 },
            new ExtractedJob { Title = "High enough", Url = "https://acme.test/2", SalaryMax = 95_000 },
            new ExtractedJob { Title = "Not stated", Url = "https://acme.test/3" },
        ], criteria);

        Assert.Equal(2, outcome.Inserted);
        Assert.Equal(1, outcome.RejectedSalary);

        await using var db = _db.CreateDbContext();
        var titles = await db.JobListings.Select(l => l.Title).OrderBy(t => t).ToListAsync();

        Assert.Equal(["High enough", "Not stated"], titles);

        var unknown = await db.JobListings.SingleAsync(l => l.Title == "Not stated");
        Assert.False(unknown.SalaryKnown);
    }

    [Fact]
    public async Task Excluded_roles_never_reach_the_database_so_they_never_cost_a_score()
    {
        var companyId = await AddCompanyAsync("Acme");

        var criteria = new MatchCriteria
        {
            DesiredRoles = [".NET developer"],
            ExcludedRoles = ["senior"],
        };

        var outcome = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            Job(".NET Developer", "https://acme.test/1"),
            Job("Senior .NET Developer", "https://acme.test/2"),
            Job("Mortgage Adviser", "https://acme.test/3"),
        ], criteria);

        Assert.Equal(1, outcome.Inserted);
        Assert.Equal(1, outcome.RejectedExcludedRole);
        Assert.Equal(1, outcome.RejectedUnwantedRole);
        Assert.Equal(2, outcome.RejectedTotal);

        await using var db = _db.CreateDbContext();
        Assert.Equal(".NET Developer", (await db.JobListings.SingleAsync()).Title);
    }

    [Fact]
    public async Task With_no_role_rules_configured_every_title_is_still_saved()
    {
        var companyId = await AddCompanyAsync("Acme");

        var outcome = await _service.UpsertForCompanyAsync(companyId, "CareerPage",
        [
            Job("Senior .NET Developer", "https://acme.test/1"),
            Job("Mortgage Adviser", "https://acme.test/2"),
        ], AnyLocationAnySalary);

        Assert.Equal(2, outcome.Inserted);
        Assert.Equal(0, outcome.RejectedTotal);
    }

    [Fact]
    public async Task Role_rules_apply_to_board_results_too()
    {
        var criteria = new MatchCriteria { ExcludedRoles = ["senior"] };

        var (listings, _) = await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult { CompanyName = "Globex", Title = "Senior Engineer", Url = "https://b.test/1" },
            new BoardJobResult { CompanyName = "Globex", Title = "Engineer", Url = "https://b.test/2" },
        ], criteria);

        Assert.Equal(1, listings.Inserted);
        Assert.Equal(1, listings.RejectedExcludedRole);
    }

    // ---- board discovery -------------------------------------------------

    [Fact]
    public async Task A_board_result_for_an_unknown_company_creates_it_as_REVIEW_with_no_careers_url()
    {
        var (listings, newCompanies) = await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult
            {
                CompanyName = "Globex Corporation",
                Title = "Engineer",
                Url = "https://adzuna.test/ad/1",
                Location = "London",
            },
        ], AnyLocationAnySalary);

        Assert.Equal(1, newCompanies);
        Assert.Equal(1, listings.Inserted);

        await using var db = _db.CreateDbContext();
        var company = await db.Companies.SingleAsync();

        Assert.Equal(CompanyStatus.REVIEW, company.Status);
        Assert.Null(company.Url);
        Assert.Equal("https://adzuna.test/ad/1", company.BoardUrl);
        Assert.Equal("Adzuna", company.Source);
    }

    [Fact]
    public async Task Board_listings_are_saved_unscored()
    {
        await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult
            {
                CompanyName = "Globex",
                Title = "Engineer",
                Url = "https://adzuna.test/ad/1",
            },
        ], AnyLocationAnySalary);

        await using var db = _db.CreateDbContext();
        var listing = await db.JobListings.SingleAsync();

        Assert.Null(listing.Score);
        Assert.Equal(JobListingStatus.New, listing.Status);
        Assert.Equal("Adzuna", listing.Source);
    }

    [Fact]
    public async Task A_company_name_that_differs_only_by_legal_suffix_is_not_duplicated()
    {
        await AddCompanyAsync("Globex Ltd");

        var (_, newCompanies) = await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult
            {
                CompanyName = "Globex Limited",
                Title = "Engineer",
                Url = "https://adzuna.test/ad/1",
            },
        ], AnyLocationAnySalary);

        Assert.Equal(0, newCompanies);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Discovery_does_not_overwrite_a_decision_I_already_made()
    {
        var companyId = await AddCompanyAsync("Globex", CompanyStatus.NOT_USE);

        await using (var seed = _db.CreateDbContext())
        {
            var existing = await seed.Companies.FindAsync(companyId);
            existing!.Url = "https://globex.test/careers";
            await seed.SaveChangesAsync();
        }

        await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult
            {
                CompanyName = "Globex",
                Title = "Engineer",
                Url = "https://adzuna.test/ad/1",
            },
        ], AnyLocationAnySalary);

        await using var db = _db.CreateDbContext();
        var company = await db.Companies.SingleAsync();

        Assert.Equal(CompanyStatus.NOT_USE, company.Status);
        Assert.Equal("https://globex.test/careers", company.Url);
    }

    [Fact]
    public async Task Several_adverts_from_one_board_company_land_under_one_company()
    {
        var (listings, newCompanies) = await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult { CompanyName = "Globex", Title = "A", Url = "https://adzuna.test/1" },
            new BoardJobResult { CompanyName = "Globex Ltd", Title = "B", Url = "https://adzuna.test/2" },
        ], AnyLocationAnySalary);

        Assert.Equal(1, newCompanies);
        Assert.Equal(2, listings.Inserted);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(2, await db.JobListings.CountAsync());
    }

    [Fact]
    public async Task An_existing_company_without_a_board_link_gains_one()
    {
        await AddCompanyAsync("Globex");

        await _service.UpsertBoardResultsAsync("Adzuna",
        [
            new BoardJobResult
            {
                CompanyName = "Globex",
                Title = "Engineer",
                Url = "https://adzuna.test/ad/1",
            },
        ], AnyLocationAnySalary);

        await using var db = _db.CreateDbContext();
        var company = await db.Companies.SingleAsync();

        Assert.Equal("https://adzuna.test/ad/1", company.BoardUrl);
        Assert.Equal(CompanyStatus.USE, company.Status); // unchanged
    }
}

using JobScout.Core.Models;
using JobScout.Core.Services;

namespace JobScout.Tests;

/// <summary>The salary and location rules. These decide what I never get to see,
/// so they are worth pinning down precisely.</summary>
public class ListingFilterTests
{
    private static MatchCriteria Criteria(
        decimal minimumSalary = 0,
        bool includeRemote = true,
        params string[] cities) =>
        new()
        {
            MinimumSalary = minimumSalary,
            Currency = "GBP",
            Cities = cities,
            IncludeRemote = includeRemote,
        };

    // ---- location -------------------------------------------------------

    [Fact]
    public void Location_in_my_cities_is_kept()
    {
        var criteria = Criteria(cities: ["London", "Manchester"]);

        Assert.True(ListingFilter.LocationMatches("London, UK", isRemote: false, criteria));
        Assert.True(ListingFilter.LocationMatches("Greater Manchester", isRemote: false, criteria));
    }

    [Fact]
    public void Location_outside_my_cities_is_rejected()
    {
        var criteria = Criteria(cities: ["London"]);

        Assert.False(ListingFilter.LocationMatches("Edinburgh", isRemote: false, criteria));
    }

    [Fact]
    public void City_match_ignores_case()
    {
        var criteria = Criteria(cities: ["london"]);

        Assert.True(ListingFilter.LocationMatches("LONDON", isRemote: false, criteria));
    }

    [Fact]
    public void No_cities_configured_means_anywhere()
    {
        var criteria = Criteria();

        Assert.True(ListingFilter.LocationMatches("Reykjavik", isRemote: false, criteria));
    }

    [Fact]
    public void Unknown_location_is_kept_rather_than_guessed_at()
    {
        var criteria = Criteria(cities: ["London"]);

        Assert.True(ListingFilter.LocationMatches(null, isRemote: false, criteria));
        Assert.True(ListingFilter.LocationMatches("   ", isRemote: false, criteria));
    }

    [Fact]
    public void Remote_is_kept_when_remote_is_allowed_even_outside_my_cities()
    {
        var criteria = Criteria(includeRemote: true, cities: ["London"]);

        Assert.True(ListingFilter.LocationMatches("Remote (US)", isRemote: false, criteria));
        Assert.True(ListingFilter.LocationMatches("Edinburgh", isRemote: true, criteria));
    }

    [Fact]
    public void Remote_is_rejected_when_remote_is_not_allowed_even_inside_my_cities()
    {
        var criteria = Criteria(includeRemote: false, cities: ["London"]);

        Assert.False(ListingFilter.LocationMatches("London (fully remote)", isRemote: false, criteria));
        Assert.False(ListingFilter.LocationMatches("London", isRemote: true, criteria));
    }

    [Theory]
    [InlineData("Remote")]
    [InlineData("100% remote")]
    [InlineData("Work from home")]
    [InlineData("Anywhere in Europe")]
    public void Remote_wording_is_recognised(string location) =>
        Assert.True(ListingFilter.LooksRemote(location));

    [Theory]
    [InlineData("London")]
    [InlineData(null)]
    [InlineData("")]
    public void Non_remote_wording_is_not_mistaken_for_remote(string? location) =>
        Assert.False(ListingFilter.LooksRemote(location));

    // ---- salary ---------------------------------------------------------

    [Fact]
    public void Salary_at_or_above_the_minimum_is_kept()
    {
        Assert.True(ListingFilter.SalaryMatches(60_000, 80_000, 70_000));
        Assert.True(ListingFilter.SalaryMatches(70_000, 70_000, 70_000));
    }

    [Fact]
    public void Salary_entirely_below_the_minimum_is_rejected()
    {
        Assert.False(ListingFilter.SalaryMatches(40_000, 50_000, 70_000));
    }

    [Fact]
    public void Top_of_the_range_is_what_counts()
    {
        // A 50-90k advert is worth seeing when I want 70k, even though the floor is lower.
        Assert.True(ListingFilter.SalaryMatches(50_000, 90_000, 70_000));
    }

    [Fact]
    public void Unknown_salary_is_kept_never_dropped()
    {
        Assert.True(ListingFilter.SalaryMatches(null, null, 70_000));
    }

    [Fact]
    public void A_minimum_of_zero_accepts_everything()
    {
        Assert.True(ListingFilter.SalaryMatches(1, 2, 0));
    }

    [Fact]
    public void Only_a_maximum_is_still_usable()
    {
        Assert.True(ListingFilter.SalaryMatches(null, 90_000, 70_000));
        Assert.False(ListingFilter.SalaryMatches(null, 50_000, 70_000));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(50_000, null, true)]
    [InlineData(null, 50_000, true)]
    [InlineData(50_000, 60_000, true)]
    public void SalaryKnown_reflects_whether_a_figure_was_found(
        int? min, int? max, bool expected) =>
        Assert.Equal(expected, ListingFilter.IsSalaryKnown(min, max));

    // ---- combined -------------------------------------------------------

    [Fact]
    public void Evaluate_reports_why_a_listing_was_dropped()
    {
        var criteria = Criteria(minimumSalary: 70_000, includeRemote: false, cities: ["London"]);

        Assert.Equal(FilterOutcome.Keep,
            ListingFilter.Evaluate("London", false, 70_000, 90_000, criteria));

        Assert.Equal(FilterOutcome.RejectedLocation,
            ListingFilter.Evaluate("Bristol", false, 90_000, 90_000, criteria));

        Assert.Equal(FilterOutcome.RejectedSalary,
            ListingFilter.Evaluate("London", false, 30_000, 40_000, criteria));
    }

    [Fact]
    public void Location_is_judged_before_salary()
    {
        // Both fail; the location reason is the one reported.
        var criteria = Criteria(minimumSalary: 70_000, cities: ["London"]);

        Assert.Equal(FilterOutcome.RejectedLocation,
            ListingFilter.Evaluate("Bristol", false, 10_000, 20_000, criteria));
    }

    [Fact]
    public void A_listing_with_no_salary_in_the_right_city_is_kept()
    {
        var criteria = Criteria(minimumSalary: 70_000, cities: ["London"]);

        Assert.Equal(FilterOutcome.Keep,
            ListingFilter.Evaluate("London", false, null, null, criteria));
    }
}

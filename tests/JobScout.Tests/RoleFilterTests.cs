using JobScout.Core.Entities;
using JobScout.Core.Models;
using JobScout.Core.Services;

namespace JobScout.Tests;

/// <summary>The two role rules from Settings: the roles I am looking for, and the words
/// that rule a role out.</summary>
public class RoleFilterTests
{
    private static MatchCriteria Criteria(string[]? wanted = null, string[]? excluded = null) => new()
    {
        DesiredRoles = wanted ?? [],
        ExcludedRoles = excluded ?? [],
    };

    // ---- matching is on whole words, punctuation folded ------------------

    [Theory]
    [InlineData(".NET Developer")]
    [InlineData(".net developer")]
    [InlineData("Senior .Net Developer")]
    [InlineData("Graduate .NET Developer (12 month FTC)")]
    public void Dot_net_developer_is_recognised_however_it_is_written(string title) =>
        Assert.True(RoleMatcher.MatchesAny(title, [".NET developer"]));

    [Fact]
    public void Word_order_matters_so_a_phrase_is_not_matched_out_of_sequence()
    {
        // Otherwise "developer .NET" would also match "NET developer tooling" and similar.
        Assert.False(RoleMatcher.MatchesAny("Developer - .NET", [".NET developer"]));
        Assert.True(RoleMatcher.MatchesAny("Developer - .NET", ["developer"]));
    }

    [Fact]
    public void Matching_ignores_case_and_punctuation() =>
        Assert.True(RoleMatcher.MatchesAny("SENIOR .NET DEVELOPER!", [".net developer"]));

    [Fact]
    public void A_term_only_matches_whole_words()
    {
        // The reason this matters: excluding "lead" must not bin a graduate scheme.
        Assert.False(RoleMatcher.MatchesAny("Leadership Development Programme", ["lead"]));
        Assert.True(RoleMatcher.MatchesAny("Tech Lead, Payments", ["lead"]));
    }

    [Fact]
    public void An_empty_term_list_matches_nothing() =>
        Assert.False(RoleMatcher.MatchesAny(".NET Developer", []));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_title_matches_nothing(string? title) =>
        Assert.False(RoleMatcher.MatchesAny(title, ["developer"]));

    [Fact]
    public void The_matching_term_can_be_reported_back()
    {
        Assert.Equal("senior", RoleMatcher.FirstMatch("Senior .NET Developer", ["principal", "senior"]));
        Assert.Null(RoleMatcher.FirstMatch("Junior Developer", ["senior"]));
    }

    // ---- exclusions ------------------------------------------------------

    [Fact]
    public void An_excluded_word_drops_the_listing() =>
        Assert.Equal(
            FilterOutcome.RejectedExcludedRole,
            ListingFilter.EvaluateTitle("Senior .NET Developer", Criteria(excluded: ["senior"])));

    [Fact]
    public void Nothing_is_excluded_when_no_exclusions_are_configured() =>
        Assert.Equal(
            FilterOutcome.Keep,
            ListingFilter.EvaluateTitle("Senior .NET Developer", Criteria()));

    // ---- wanted roles ----------------------------------------------------

    [Fact]
    public void A_wanted_role_is_kept() =>
        Assert.Equal(
            FilterOutcome.Keep,
            ListingFilter.EvaluateTitle(".NET Developer", Criteria(wanted: [".NET developer"])));

    [Fact]
    public void A_title_matching_no_wanted_role_is_dropped() =>
        Assert.Equal(
            FilterOutcome.RejectedUnwantedRole,
            ListingFilter.EvaluateTitle("Mortgage Adviser", Criteria(wanted: [".NET developer"])));

    [Fact]
    public void No_wanted_roles_configured_means_every_title_is_in_scope()
    {
        // The setting must never be read as "want nothing".
        Assert.Equal(FilterOutcome.Keep, ListingFilter.EvaluateTitle("Mortgage Adviser", Criteria()));
    }

    [Fact]
    public void An_unreadable_title_is_kept_for_the_ai_to_judge() =>
        Assert.Equal(
            FilterOutcome.Keep,
            ListingFilter.EvaluateTitle(null, Criteria(wanted: [".NET developer"])));

    [Fact]
    public void Any_one_of_several_wanted_roles_is_enough()
    {
        var criteria = Criteria(wanted: [".NET developer", "backend engineer"]);

        Assert.Equal(FilterOutcome.Keep, ListingFilter.EvaluateTitle("Backend Engineer", criteria));
        Assert.Equal(FilterOutcome.Keep, ListingFilter.EvaluateTitle(".NET Developer", criteria));
    }

    // ---- the two rules together -----------------------------------------

    [Fact]
    public void An_exclusion_beats_a_wanted_role()
    {
        // The case from the brief: wanted ".NET developer", excluded "senior".
        var criteria = Criteria(wanted: [".NET developer"], excluded: ["senior"]);

        Assert.Equal(
            FilterOutcome.RejectedExcludedRole,
            ListingFilter.EvaluateTitle("Senior .NET Developer", criteria));

        Assert.Equal(
            FilterOutcome.Keep,
            ListingFilter.EvaluateTitle(".NET Developer", criteria));
    }

    [Fact]
    public void Title_rules_are_applied_before_location_and_salary()
    {
        // Cheapest test first, and it is the one most likely to rule a listing out.
        var criteria = new MatchCriteria
        {
            MinimumSalary = 70_000,
            Cities = ["London"],
            ExcludedRoles = ["senior"],
        };

        Assert.Equal(
            FilterOutcome.RejectedExcludedRole,
            ListingFilter.Evaluate("Senior Developer", "Bristol", false, 10_000, 20_000, criteria));
    }

    [Fact]
    public void The_full_filter_still_applies_location_and_salary_to_a_wanted_role()
    {
        var criteria = new MatchCriteria
        {
            MinimumSalary = 70_000,
            Cities = ["London"],
            IncludeRemote = false,
            DesiredRoles = [".NET developer"],
        };

        Assert.Equal(
            FilterOutcome.Keep,
            ListingFilter.Evaluate(".NET Developer", "London", false, 80_000, 90_000, criteria));

        Assert.Equal(
            FilterOutcome.RejectedLocation,
            ListingFilter.Evaluate(".NET Developer", "Bristol", false, 80_000, 90_000, criteria));

        Assert.Equal(
            FilterOutcome.RejectedSalary,
            ListingFilter.Evaluate(".NET Developer", "London", false, 30_000, 40_000, criteria));
    }

    // ---- the settings round-trip ----------------------------------------

    [Fact]
    public void The_config_round_trips_both_lists()
    {
        var config = new AppConfig();

        config.SetDesiredRoles([".NET developer", "  backend engineer  ", "", "   "]);
        config.SetExcludedRoles(["senior", "principal"]);

        Assert.Equal([".NET developer", "backend engineer"], config.DesiredRoleList);
        Assert.Equal(["senior", "principal"], config.ExcludedRoleList);
    }

    // ---- the comma-separated list format --------------------------------

    [Fact]
    public void Lists_are_stored_comma_separated()
    {
        var config = new AppConfig();

        config.SetDesiredRoles([".NET developer", "backend engineer"]);
        config.SetCities(["London", "Manchester"]);

        Assert.Equal(".NET developer, backend engineer", config.DesiredRoles);
        Assert.Equal("London, Manchester", config.Cities);
    }

    [Theory]
    [InlineData(".NET developer,backend engineer")]
    [InlineData(".NET developer, backend engineer")]
    [InlineData("  .NET developer ,  backend engineer  ")]
    [InlineData(".NET developer,,backend engineer,")]
    public void Spacing_and_stray_commas_do_not_matter(string entered) =>
        Assert.Equal([".NET developer", "backend engineer"], AppConfig.ParseList(entered));

    [Fact]
    public void Newlines_are_still_accepted_so_a_pasted_column_works()
    {
        // Also keeps rows written before these fields were comma-separated readable.
        var parsed = AppConfig.ParseList("senior\nprincipal\r\nmanager");

        Assert.Equal(["senior", "principal", "manager"], parsed);
    }

    [Fact]
    public void A_config_stored_in_the_old_newline_format_still_reads_correctly()
    {
        var config = new AppConfig { DesiredRoles = ".NET developer\nbackend engineer" };

        Assert.Equal([".NET developer", "backend engineer"], config.DesiredRoleList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void A_list_of_nothing_is_empty(string? entered) =>
        Assert.Empty(AppConfig.ParseList(entered));

    [Fact]
    public void A_single_entry_needs_no_comma() =>
        Assert.Equal([".NET developer"], AppConfig.ParseList(".NET developer"));

    [Fact]
    public void Round_tripping_through_the_stored_format_is_stable()
    {
        var first = AppConfig.FormatList([".NET developer", "backend engineer"]);
        var parsed = AppConfig.ParseList(first);

        Assert.Equal(first, AppConfig.FormatList(parsed));
    }

    [Fact]
    public void An_empty_config_yields_empty_lists()
    {
        var config = new AppConfig();

        Assert.Empty(config.DesiredRoleList);
        Assert.Empty(config.ExcludedRoleList);
    }
}

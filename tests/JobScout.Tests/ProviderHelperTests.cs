using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Ai;
using JobScout.Infrastructure.Boards;
using JobScout.Infrastructure.Fetching;
using JobScout.Infrastructure.Services;

namespace JobScout.Tests;

/// <summary>The parsing and normalising the providers rely on. All pure, all cheap.</summary>
public class JsonExtractionTests
{
    private sealed class Score
    {
        public int? score { get; set; }
        public string? reasoning { get; set; }
    }

    [Fact]
    public void Plain_json_is_read()
    {
        var result = JsonExtraction.Deserialise<Score>("""{"score": 82, "reasoning": "good"}""");

        Assert.Equal(82, result!.score);
    }

    [Fact]
    public void A_fenced_block_is_unwrapped()
    {
        var result = JsonExtraction.Deserialise<Score>(
            "```json\n{\"score\": 82, \"reasoning\": \"good\"}\n```");

        Assert.Equal(82, result!.score);
    }

    [Fact]
    public void Chattiness_around_the_json_is_ignored()
    {
        var result = JsonExtraction.Deserialise<Score>(
            "Sure! Here is the result:\n{\"score\": 55}\nLet me know if you need more.");

        Assert.Equal(55, result!.score);
    }

    [Fact]
    public void Braces_inside_a_string_do_not_end_the_object()
    {
        var result = JsonExtraction.Deserialise<Score>(
            """{"score": 40, "reasoning": "uses {braces} and \"quotes\" in the text"}""");

        Assert.Equal(40, result!.score);
        Assert.Contains("braces", result.reasoning);
    }

    [Fact]
    public void Nested_objects_are_balanced_correctly()
    {
        var json = JsonExtraction.ExtractJson("""prefix {"a": {"b": {"c": 1}}} suffix""");

        Assert.Equal("""{"a": {"b": {"c": 1}}}""", json);
    }

    [Theory]
    [InlineData("I cannot help with that.")]
    [InlineData("")]
    [InlineData(null)]
    public void Text_with_no_json_returns_null(string? text) =>
        Assert.Null(JsonExtraction.Deserialise<Score>(text));

    [Fact]
    public void Malformed_json_returns_null_rather_than_throwing() =>
        Assert.Null(JsonExtraction.Deserialise<Score>("""{"score": }"""));

    [Fact]
    public void An_unterminated_object_returns_null() =>
        Assert.Null(JsonExtraction.Deserialise<Score>("""{"score": 12"""));
}

public class CompanyNameNormaliserTests
{
    [Theory]
    [InlineData("Acme Ltd", "acme")]
    [InlineData("Acme Limited", "acme")]
    [InlineData("ACME, Inc.", "acme")]
    [InlineData("Acme Group Ltd", "acme")]
    [InlineData("  Acme  ", "acme")]
    [InlineData("Acme & Co", "acme")]
    public void Legal_suffixes_and_punctuation_collapse(string input, string expected) =>
        Assert.Equal(expected, CompanyNameNormaliser.Normalise(input));

    [Fact]
    public void Accents_are_folded() =>
        Assert.Equal("societe generale", CompanyNameNormaliser.Normalise("Société Générale"));

    [Fact]
    public void Genuinely_different_companies_stay_different() =>
        Assert.NotEqual(
            CompanyNameNormaliser.Normalise("Acme Robotics"),
            CompanyNameNormaliser.Normalise("Acme Foods"));

    [Fact]
    public void A_name_that_is_only_a_suffix_is_not_emptied() =>
        Assert.Equal("group", CompanyNameNormaliser.Normalise("Group"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_gives_nothing_out(string? input) =>
        Assert.Equal(string.Empty, CompanyNameNormaliser.Normalise(input));
}

public class UrlNormalisationTests
{
    [Theory]
    [InlineData("https://Example.com/Jobs/1", "https://example.com/Jobs/1")]
    [InlineData("https://example.com/jobs/1/", "https://example.com/jobs/1")]
    [InlineData("https://example.com/jobs/1#apply", "https://example.com/jobs/1")]
    [InlineData("https://example.com:443/jobs/1", "https://example.com/jobs/1")]
    [InlineData("http://example.com:80/jobs/1", "http://example.com/jobs/1")]
    public void Cosmetic_differences_are_removed(string input, string expected) =>
        Assert.Equal(expected, ListingUpsertService.NormaliseUrl(input));

    [Fact]
    public void The_path_case_is_preserved_because_paths_can_be_case_sensitive() =>
        Assert.Contains("/Jobs/", ListingUpsertService.NormaliseUrl("https://example.com/Jobs/1"));

    [Fact]
    public void A_query_string_is_kept_because_boards_put_the_advert_id_there() =>
        Assert.Contains("?id=42", ListingUpsertService.NormaliseUrl("https://example.com/jobs?id=42"));

    [Fact]
    public void Something_that_is_not_a_url_is_returned_as_is() =>
        Assert.Equal("not a url", ListingUpsertService.NormaliseUrl("  not a url  "));
}

public class RelativeUrlResolutionTests
{
    [Theory]
    [InlineData("/jobs/1", "https://acme.test/careers", "https://acme.test/jobs/1")]
    [InlineData("jobs/1", "https://acme.test/careers/", "https://acme.test/careers/jobs/1")]
    [InlineData("https://other.test/x", "https://acme.test/careers", "https://other.test/x")]
    public void Relative_advert_links_are_resolved_against_the_page(
        string href, string pageUrl, string expected) =>
        Assert.Equal(expected, AiJobMatcher.ResolveUrl(href, pageUrl));

    [Fact]
    public void A_missing_link_falls_back_to_the_page_itself() =>
        Assert.Equal("https://acme.test/careers", AiJobMatcher.ResolveUrl(null, "https://acme.test/careers"));

    [Theory]
    [InlineData("javascript:void(0)")]
    [InlineData("mailto:jobs@acme.test")]
    public void Non_http_schemes_are_rejected(string href) =>
        Assert.Null(AiJobMatcher.ResolveUrl(href, "https://acme.test/careers"));
}

public class RobotsRulesTests
{
    private static bool IsAllowed(string robotsTxt, string path)
    {
        var rules = RobotsGate.CachedRules.Parse(robotsTxt, "JobScout/1.0 (test)");
        return rules.IsAllowed(path);
    }

    [Fact]
    public void An_empty_robots_file_allows_everything() =>
        Assert.True(IsAllowed("", "/careers"));

    [Fact]
    public void A_disallowed_path_is_blocked() =>
        Assert.False(IsAllowed("User-agent: *\nDisallow: /careers", "/careers/engineer"));

    [Fact]
    public void An_unrelated_disallow_does_not_block_us() =>
        Assert.True(IsAllowed("User-agent: *\nDisallow: /admin", "/careers"));

    [Fact]
    public void A_bare_disallow_means_allow_everything() =>
        Assert.True(IsAllowed("User-agent: *\nDisallow:", "/careers"));

    [Fact]
    public void Disallow_slash_blocks_the_whole_site() =>
        Assert.False(IsAllowed("User-agent: *\nDisallow: /", "/careers"));

    [Fact]
    public void A_more_specific_allow_beats_a_broader_disallow() =>
        Assert.True(IsAllowed("User-agent: *\nDisallow: /\nAllow: /careers", "/careers/engineer"));

    [Fact]
    public void A_group_naming_us_wins_over_the_star_group()
    {
        const string robots = """
            User-agent: *
            Disallow: /

            User-agent: JobScout
            Allow: /careers
            Disallow: /private
            """;

        Assert.True(IsAllowed(robots, "/careers"));
        Assert.False(IsAllowed(robots, "/private"));
    }

    [Fact]
    public void Comments_are_ignored() =>
        Assert.False(IsAllowed("# a comment\nUser-agent: *\nDisallow: /careers # inline", "/careers"));

    [Fact]
    public void Wildcards_are_honoured() =>
        Assert.False(IsAllowed("User-agent: *\nDisallow: /*/apply", "/careers/apply"));

    [Fact]
    public void An_end_anchor_only_matches_the_whole_path()
    {
        const string robots = "User-agent: *\nDisallow: /careers$";

        Assert.False(IsAllowed(robots, "/careers"));
        Assert.True(IsAllowed(robots, "/careers/engineer"));
    }

    [Fact]
    public void Consecutive_user_agent_lines_share_one_group()
    {
        const string robots = """
            User-agent: SomeBot
            User-agent: *
            Disallow: /careers
            """;

        Assert.False(IsAllowed(robots, "/careers"));
    }
}

public class JavaScriptHeuristicTests
{
    private const int MinText = 600;

    [Fact]
    public void An_empty_react_shell_is_treated_as_javascript_rendered()
    {
        const string html = """<html><body><div id="root"></div></body></html>""";

        Assert.True(HtmlText.LooksJavaScriptRendered(html, "", MinText));
    }

    [Fact]
    public void A_server_rendered_careers_page_is_taken_at_face_value()
    {
        var text = string.Join(" ", Enumerable.Repeat("We are hiring for this job vacancy role.", 30));
        var html = $"<html><body>{text}</body></html>";

        Assert.False(HtmlText.LooksJavaScriptRendered(html, text, MinText));
    }

    [Fact]
    public void A_page_with_plenty_of_text_but_nothing_job_like_is_suspicious()
    {
        // Short, no job words: worth a browser attempt.
        var text = new string('x', 100);

        Assert.True(HtmlText.LooksJavaScriptRendered("<html><body>x</body></html>", text, MinText));
    }

    [Fact]
    public void Text_is_extracted_without_scripts_or_styles()
    {
        const string html = """
            <html><head><style>body{color:red}</style></head>
            <body><script>var x = 1;</script><h1>Careers</h1><p>Engineer</p></body></html>
            """;

        var text = HtmlText.ExtractText(html, 10_000);

        Assert.Contains("Careers", text);
        Assert.Contains("Engineer", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact]
    public void Anchor_targets_are_inlined_so_the_model_can_pair_titles_with_links()
    {
        const string html = """<html><body><a href="/jobs/1">Engineer</a></body></html>""";

        var text = HtmlText.ExtractTextWithLinks(html, "https://acme.test/careers", 10_000);

        Assert.Contains("Engineer", text);
        Assert.Contains("https://acme.test/jobs/1", text);
    }

    [Fact]
    public void Anchors_that_go_nowhere_useful_are_not_inlined()
    {
        const string html =
            """<html><body><a href="#top">Top</a><a href="mailto:a@b.test">Mail</a></body></html>""";

        var text = HtmlText.ExtractTextWithLinks(html, "https://acme.test/careers", 10_000);

        Assert.DoesNotContain("mailto:", text);
        Assert.DoesNotContain("#top", text);
    }
}

public class AdzunaUrlTests
{
    private static AdzunaJobBoardProvider Provider(AdzunaOptions config) =>
        new(new StubHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(new JobScoutOptions
            {
                Boards = new BoardOptions { Adzuna = config },
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdzunaJobBoardProvider>.Instance);

    private static AdzunaOptions Config => new()
    {
        Enabled = true,
        AppId = "the-id",
        AppKey = "the-key",
        Country = "gb",
    };

    [Fact]
    public void The_search_url_carries_the_query_country_and_credentials()
    {
        var url = Provider(Config).BuildUrl(new BoardSearchRequest
        {
            Query = "data engineering",
            City = "London",
            MinimumSalary = 70_000,
        });

        Assert.StartsWith("https://api.adzuna.com/v1/api/jobs/gb/search/1?", url);
        Assert.Contains("what=data%20engineering", url);
        Assert.Contains("where=London", url);
        Assert.Contains("salary_min=70000", url);
        Assert.Contains("app_id=the-id", url);
    }

    [Fact]
    public void Adverts_without_a_stated_salary_are_asked_for_too()
    {
        // Otherwise the board would hide exactly the listings we deliberately keep and flag.
        var url = Provider(Config).BuildUrl(new BoardSearchRequest { Query = "x", MinimumSalary = 50_000 });

        Assert.Contains("salary_include_unknown=1", url);
    }

    [Fact]
    public void A_nationwide_search_omits_the_location()
    {
        var url = Provider(Config).BuildUrl(new BoardSearchRequest { Query = "x", City = null });

        Assert.DoesNotContain("where=", url);
    }

    [Fact]
    public void A_board_with_no_keys_reports_itself_as_disabled()
    {
        Assert.False(Provider(new AdzunaOptions { Enabled = true }).IsEnabled);
        Assert.False(Provider(new AdzunaOptions { AppId = "the-id", AppKey = "the-key" }).IsEnabled);
        Assert.True(Provider(Config).IsEnabled);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

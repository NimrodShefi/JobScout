using JobScout.Core.Options;
using JobScout.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>The AI configuration is not optional - scoring is the point of the app - so
/// these rules are what stops it starting with a configuration that cannot work.</summary>
public class AiOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(AiOptions ai) =>
        new AiOptionsValidator().Validate(null, new JobScoutOptions { Ai = ai });

    private static void AssertFailsWith(AiOptions ai, string expectedFragment)
    {
        var result = Validate(ai);

        Assert.True(result.Failed, "Expected the configuration to be rejected.");
        Assert.Contains(result.Failures!, f => f.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    private static AiOptions Valid(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => new AiOptions { Provider = provider, ApiKey = "sk-ant-key" },
        AiProvider.OpenAI => new AiOptions { Provider = provider, ApiKey = "sk-key" },
        AiProvider.AzureOpenAI => new AiOptions
        {
            Provider = provider,
            ApiKey = "azure-key",
            Endpoint = "https://my-resource.openai.azure.com/",
        },
        AiProvider.Ollama => new AiOptions { Provider = provider },
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    // ---- the configurations that should be accepted ---------------------

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    [InlineData(AiProvider.AzureOpenAI)]
    [InlineData(AiProvider.Ollama)]
    public void A_properly_configured_provider_is_accepted(AiProvider provider) =>
        Assert.True(Validate(Valid(provider)).Succeeded);

    [Fact]
    public void Ollama_needs_no_api_key_because_it_runs_locally()
    {
        var result = Validate(new AiOptions { Provider = AiProvider.Ollama, ApiKey = null });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_blank_model_is_allowed_because_a_per_provider_default_is_applied()
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.Model = null;

        Assert.True(Validate(ai).Succeeded);
    }

    [Fact]
    public void An_optional_endpoint_may_be_omitted_for_providers_that_do_not_need_one()
    {
        var ai = Valid(AiProvider.OpenAI);
        ai.Endpoint = "";

        Assert.True(Validate(ai).Succeeded);
    }

    // ---- missing credentials --------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_api_key_stops_the_app(string? apiKey)
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.ApiKey = apiKey;

        AssertFailsWith(ai, "JobScout:Ai:ApiKey");
    }

    [Fact]
    public void The_missing_key_message_names_the_provider_and_the_command_that_fixes_it()
    {
        var ai = Valid(AiProvider.OpenAI);
        ai.ApiKey = null;

        var failure = Assert.Single(Validate(ai).Failures!);

        Assert.Contains("OpenAI", failure);
        Assert.Contains("dotnet user-secrets set", failure);
        Assert.Contains("JobScout:Ai:ApiKey", failure);
    }

    [Fact]
    public void A_key_pasted_with_stray_whitespace_is_rejected_rather_than_sent()
    {
        // Otherwise this surfaces much later as an unexplained 401 from the provider.
        var ai = Valid(AiProvider.Anthropic);
        ai.ApiKey = "sk-ant-key\n";

        AssertFailsWith(ai, "whitespace");
    }

    // ---- endpoints -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Azure_without_an_endpoint_stops_the_app(string? endpoint)
    {
        var ai = Valid(AiProvider.AzureOpenAI);
        ai.Endpoint = endpoint;

        AssertFailsWith(ai, "JobScout:Ai:Endpoint");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("my-resource.openai.azure.com")]
    [InlineData("ftp://example.com")]
    public void A_malformed_endpoint_is_rejected(string endpoint)
    {
        var ai = Valid(AiProvider.AzureOpenAI);
        ai.Endpoint = endpoint;

        AssertFailsWith(ai, "absolute http(s) URL");
    }

    [Fact]
    public void A_plain_http_endpoint_is_fine_because_ollama_is_served_over_it()
    {
        var ai = Valid(AiProvider.Ollama);
        ai.Endpoint = "http://localhost:11434";

        Assert.True(Validate(ai).Succeeded);
    }

    // ---- limits ----------------------------------------------------------

    [Fact]
    public void A_negative_retry_count_is_rejected()
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.MaxJsonRetries = -1;

        AssertFailsWith(ai, "MaxJsonRetries");
    }

    [Fact]
    public void Zero_retries_is_allowed_because_it_means_one_attempt()
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.MaxJsonRetries = 0;

        Assert.True(Validate(ai).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void A_non_positive_timeout_is_rejected(int seconds)
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.TimeoutSeconds = seconds;

        AssertFailsWith(ai, "TimeoutSeconds");
    }

    [Fact]
    public void Truncating_the_job_text_to_nothing_is_rejected()
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.MaxDescriptionChars = 0;

        AssertFailsWith(ai, "MaxDescriptionChars");
    }

    [Fact]
    public void Truncating_the_cv_to_nothing_is_rejected()
    {
        var ai = Valid(AiProvider.Anthropic);
        ai.MaxCvChars = 0;

        AssertFailsWith(ai, "MaxCvChars");
    }

    // ---- reporting -------------------------------------------------------

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_one_per_restart()
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.AzureOpenAI,
            ApiKey = null,
            Endpoint = null,
            TimeoutSeconds = 0,
        };

        var failures = Validate(ai).Failures!.ToList();

        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, f => f.Contains("ApiKey"));
        Assert.Contains(failures, f => f.Contains("Endpoint"));
        Assert.Contains(failures, f => f.Contains("TimeoutSeconds"));
    }

    [Fact]
    public void An_unrecognised_provider_is_reported_on_its_own()
    {
        // Nothing else can be judged without knowing which provider is meant.
        var ai = new AiOptions { Provider = (AiProvider)99, ApiKey = null };

        var failure = Assert.Single(Validate(ai).Failures!);

        Assert.Contains("Provider", failure);
        Assert.Contains("Anthropic", failure);
    }

    [Fact]
    public void No_failure_message_ever_echoes_the_key_back()
    {
        const string secret = "sk-ant-do-not-log-me";

        var ai = new AiOptions
        {
            Provider = AiProvider.AzureOpenAI,
            ApiKey = secret,
            Endpoint = "nonsense",
        };

        Assert.All(Validate(ai).Failures!, f => Assert.DoesNotContain(secret, f));
    }

    [Fact]
    public void The_shipped_defaults_would_be_rejected_without_a_key()
    {
        // appsettings.json ships with an empty ApiKey on purpose - this is the failure a
        // new user should see on their very first run, before anything else happens.
        var shipped = new AiOptions
        {
            Provider = AiProvider.Anthropic,
            Model = "claude-sonnet-5",
            ApiKey = "",
        };

        AssertFailsWith(shipped, "required for the Anthropic provider");
    }
}

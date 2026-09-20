using JobScout.Core.Options;
using JobScout.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace JobScout.Tests;

/// <summary>Half-finished switches between providers. These configurations are each
/// internally plausible but would call the wrong service, so they must be refused at
/// start-up rather than failing later with an authentication error.</summary>
public class AiOptionsMisconfigurationTests
{
    private static ValidateOptionsResult Validate(AiOptions ai) =>
        new AiOptionsValidator().Validate(null, new JobScoutOptions { Ai = ai });

    [Fact]
    public void An_azure_endpoint_left_behind_on_anthropic_is_refused()
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.Anthropic,
            Model = "gpt-5.3-codex",
            ApiKey = "an-azure-key",
            Endpoint = "https://my-resource.services.ai.azure.com/openai/v1/responses",
        };

        var result = Validate(ai);

        Assert.True(result.Failed);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("does not use a custom endpoint", failure);
        Assert.Contains("AzureOpenAI", failure);
        Assert.Contains("OpenAI", failure);
    }

    [Fact]
    public void The_same_settings_are_accepted_once_the_provider_says_OpenAI()
    {
        // An OpenAI-compatible endpoint is exactly what the OpenAI provider is for.
        var ai = new AiOptions
        {
            Provider = AiProvider.OpenAI,
            Model = "gpt-5.3-codex",
            ApiKey = "an-azure-key",
            Endpoint = "https://my-resource.services.ai.azure.com/openai/v1",
        };

        Assert.True(Validate(ai).Succeeded);
    }

    [Fact]
    public void An_azure_endpoint_pointing_at_an_operation_rather_than_the_resource_is_refused()
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.AzureOpenAI,
            ApiKey = "azure-key",
            Endpoint = "https://my-resource.services.ai.azure.com/openai/v1/responses",
        };

        var failure = Assert.Single(Validate(ai).Failures!);

        Assert.Contains("resource root", failure);
        Assert.Contains("https://my-resource.services.ai.azure.com/", failure);
    }

    [Theory]
    [InlineData("https://my-resource.openai.azure.com/")]
    [InlineData("https://my-resource.openai.azure.com")]
    public void An_azure_resource_root_is_accepted_with_or_without_the_trailing_slash(string endpoint)
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.AzureOpenAI,
            ApiKey = "azure-key",
            Endpoint = endpoint,
        };

        Assert.True(Validate(ai).Succeeded);
    }

    [Theory]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAI)]
    [InlineData(AiProvider.Ollama)]
    public void A_deployment_name_on_a_non_azure_provider_is_refused(AiProvider provider)
    {
        var ai = new AiOptions
        {
            Provider = provider,
            ApiKey = "key",
            DeploymentName = "my-deployment",
        };

        Assert.Contains(Validate(ai).Failures!, f => f.Contains("DeploymentName"));
    }

    [Fact]
    public void A_deployment_name_is_fine_on_azure()
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.AzureOpenAI,
            ApiKey = "azure-key",
            Endpoint = "https://my-resource.openai.azure.com/",
            DeploymentName = "my-deployment",
        };

        Assert.True(Validate(ai).Succeeded);
    }

    [Fact]
    public void An_ollama_endpoint_keeps_its_path_because_ollama_serves_a_v1_prefix()
    {
        var ai = new AiOptions
        {
            Provider = AiProvider.Ollama,
            Endpoint = "http://localhost:11434/v1",
        };

        Assert.True(Validate(ai).Succeeded);
    }
}

using System.ClientModel;
using Anthropic.SDK;
using JobScout.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ai;

/// <summary>Builds the single <see cref="IChatClient"/> the matcher runs on.
/// Swapping provider is a config change; swapping in a brand new provider is one case here.</summary>
public static class ChatClientFactory
{
    public static IChatClient Create(IOptions<JobScoutOptions> options, ILoggerFactory loggerFactory)
    {
        var ai = options.Value.Ai;
        var model = string.IsNullOrWhiteSpace(ai.Model) ? DefaultModel(ai.Provider) : ai.Model;

        IChatClient inner = ai.Provider switch
        {
            AiProvider.Anthropic => CreateAnthropic(ai, model),
            AiProvider.OpenAI => CreateOpenAI(ai, model),
            AiProvider.AzureOpenAI => CreateAzureOpenAI(ai, model),
            AiProvider.Ollama => CreateOllama(ai, model),
            _ => throw new InvalidOperationException($"Unsupported AI provider '{ai.Provider}'."),
        };

        return new ChatClientBuilder(inner)
            // Every provider takes its model from ChatOptions, so set it once here.
            .ConfigureOptions(o => o.ModelId ??= model)
            .UseLogging(loggerFactory)
            .Build();
    }

    public static string DefaultModel(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "claude-sonnet-5",
        AiProvider.OpenAI => "gpt-4.1-mini",
        AiProvider.AzureOpenAI => "gpt-4.1-mini",
        AiProvider.Ollama => "llama3.1",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static IChatClient CreateAnthropic(AiOptions ai, string model)
    {
        RequireKey(ai, "JobScout:Ai:ApiKey");
        _ = model; // applied by ConfigureOptions above
        var client = new AnthropicClient(new APIAuthentication(ai.ApiKey));

        // MessagesEndpoint implements IChatClient explicitly.
        return client.Messages;
    }

    private static IChatClient CreateOpenAI(AiOptions ai, string model)
    {
        RequireKey(ai, "JobScout:Ai:ApiKey");

        var credential = new ApiKeyCredential(ai.ApiKey!);
        var clientOptions = new OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(ai.Endpoint))
            clientOptions.Endpoint = new Uri(ai.Endpoint);

        return new OpenAI.OpenAIClient(credential, clientOptions)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAI(AiOptions ai, string model)
    {
        RequireKey(ai, "JobScout:Ai:ApiKey");

        if (string.IsNullOrWhiteSpace(ai.Endpoint))
            throw new InvalidOperationException(
                "Azure OpenAI needs JobScout:Ai:Endpoint, e.g. https://my-resource.openai.azure.com/.");

        var deployment = string.IsNullOrWhiteSpace(ai.DeploymentName) ? model : ai.DeploymentName;

        return new Azure.AI.OpenAI.AzureOpenAIClient(
                new Uri(ai.Endpoint),
                new ApiKeyCredential(ai.ApiKey!))
            .GetChatClient(deployment)
            .AsIChatClient();
    }

    /// <summary>Ollama is reached through its OpenAI-compatible endpoint, so no extra package
    /// and no separate code path are needed.</summary>
    private static IChatClient CreateOllama(AiOptions ai, string model)
    {
        var endpoint = string.IsNullOrWhiteSpace(ai.Endpoint)
            ? "http://localhost:11434/v1"
            : ai.Endpoint.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? ai.Endpoint
                : ai.Endpoint.TrimEnd('/') + "/v1";

        // Ollama ignores the key but the client insists on one.
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(ai.ApiKey) ? "ollama" : ai.ApiKey);

        return new OpenAI.OpenAIClient(credential, new OpenAI.OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static void RequireKey(AiOptions ai, string configPath)
    {
        if (string.IsNullOrWhiteSpace(ai.ApiKey))
            throw new InvalidOperationException(
                $"No API key configured for {ai.Provider}. Set '{configPath}' with: " +
                $"dotnet user-secrets set \"{configPath}\" \"<key>\"");
    }
}

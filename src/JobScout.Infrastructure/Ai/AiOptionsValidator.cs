using JobScout.Core.Options;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ai;

/// <summary>Checks the AI configuration before the app is allowed to serve anything.
///
/// Scoring is the point of JobScout, so a missing or malformed AI configuration is a
/// start-up failure, not something to discover hours later in a failed run. Every message
/// names the exact configuration key and, for secrets, the command that sets it.</summary>
public sealed class AiOptionsValidator : IValidateOptions<JobScoutOptions>
{
    private const string Section = "JobScout:Ai";

    public ValidateOptionsResult Validate(string? name, JobScoutOptions options)
    {
        var ai = options.Ai;
        var failures = new List<string>();

        if (!Enum.IsDefined(ai.Provider))
        {
            failures.Add(
                $"'{Section}:Provider' is not a recognised provider. " +
                $"Use one of: {string.Join(", ", Enum.GetNames<AiProvider>())}.");

            // Nothing below can be judged without knowing the provider.
            return ValidateOptionsResult.Fail(failures);
        }

        ValidateCredentials(ai, failures);
        ValidateEndpoint(ai, failures);
        ValidateSettingsTheProviderWouldIgnore(ai, failures);
        ValidateLimits(ai, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateCredentials(AiOptions ai, List<string> failures)
    {
        // Ollama runs locally and authenticates nobody.
        if (ai.Provider == AiProvider.Ollama) return;

        if (string.IsNullOrWhiteSpace(ai.ApiKey))
        {
            failures.Add(
                $"'{Section}:ApiKey' is required for the {ai.Provider} provider. Set it with: " +
                $"dotnet user-secrets set \"{Section}:ApiKey\" \"<your key>\" --project src/JobScout.Web");
        }
        else if (ai.ApiKey.Trim() != ai.ApiKey)
        {
            // A key pasted with a stray newline fails at the provider with a useless 401.
            failures.Add($"'{Section}:ApiKey' has leading or trailing whitespace. Remove it and set the key again.");
        }
    }

    private static void ValidateEndpoint(AiOptions ai, List<string> failures)
    {
        var endpoint = ai.Endpoint;
        var hasEndpoint = !string.IsNullOrWhiteSpace(endpoint);

        if (ai.Provider == AiProvider.AzureOpenAI && !hasEndpoint)
        {
            failures.Add(
                $"'{Section}:Endpoint' is required for the AzureOpenAI provider, " +
                "e.g. https://my-resource.openai.azure.com/.");
            return;
        }

        if (!hasEndpoint) return;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            failures.Add($"'{Section}:Endpoint' must be an absolute http(s) URL. It is currently '{endpoint}'.");
            return;
        }

        // The Azure client appends its own API path, so it wants the resource root. Given a
        // full operation URL it would build something like /openai/v1/responses/openai/... and
        // fail with a 404 that points nowhere useful.
        if (ai.Provider == AiProvider.AzureOpenAI && uri.AbsolutePath.Trim('/').Length > 0)
        {
            failures.Add(
                $"'{Section}:Endpoint' should be the Azure resource root, not a full operation URL. " +
                $"Use '{uri.GetLeftPart(UriPartial.Authority)}/' instead of '{endpoint}'. " +
                "If you meant to call an OpenAI-compatible endpoint directly, set " +
                $"'{Section}:Provider' to 'OpenAI' instead.");
        }
    }

    /// <summary>A setting the chosen provider does not read is almost always a half-finished
    /// switch between providers - an Azure endpoint left behind on Anthropic, say. Ignoring
    /// it silently means the app starts, calls the wrong service and fails much later with
    /// an authentication error that explains nothing.</summary>
    private static void ValidateSettingsTheProviderWouldIgnore(AiOptions ai, List<string> failures)
    {
        if (ai.Provider == AiProvider.Anthropic && !string.IsNullOrWhiteSpace(ai.Endpoint))
        {
            failures.Add(
                $"'{Section}:Endpoint' is set to '{ai.Endpoint}', but the Anthropic provider does not " +
                "use a custom endpoint and would ignore it. Clear the endpoint, or change " +
                $"'{Section}:Provider' to the one that endpoint belongs to - 'AzureOpenAI' for an " +
                "Azure resource root, or 'OpenAI' for any OpenAI-compatible endpoint.");
        }

        if (ai.Provider != AiProvider.AzureOpenAI && !string.IsNullOrWhiteSpace(ai.DeploymentName))
        {
            failures.Add(
                $"'{Section}:DeploymentName' only applies to the AzureOpenAI provider and would be " +
                $"ignored by {ai.Provider}. Clear it, or switch the provider.");
        }
    }

    private static void ValidateLimits(AiOptions ai, List<string> failures)
    {
        if (ai.MaxJsonRetries < 0)
            failures.Add($"'{Section}:MaxJsonRetries' cannot be negative.");

        if (ai.TimeoutSeconds <= 0)
            failures.Add($"'{Section}:TimeoutSeconds' must be greater than zero.");

        // Truncating the prompt to nothing would send the model an empty job or CV.
        if (ai.MaxDescriptionChars <= 0)
            failures.Add($"'{Section}:MaxDescriptionChars' must be greater than zero.");

        if (ai.MaxCvChars <= 0)
            failures.Add($"'{Section}:MaxCvChars' must be greater than zero.");
    }
}

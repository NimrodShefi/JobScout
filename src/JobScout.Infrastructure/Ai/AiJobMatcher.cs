using System.Text;
using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ai;

/// <summary>The one AI-facing service. Everything it sends is truncated by configuration,
/// and nothing it sends (CV text, job text, email snippets) is ever logged.</summary>
public sealed class AiJobMatcher(
    IChatClient chatClient,
    IOptions<JobScoutOptions> options,
    ILogger<AiJobMatcher> logger) : IJobMatcher
{
    private AiOptions Ai => options.Value.Ai;

    public async Task<JobMatchResult?> ScoreAsync(
        string cvText,
        MatchCriteria criteria,
        string jobTitle,
        string? jobLocation,
        string jobDescription,
        CancellationToken ct = default)
    {
        var prompt = new StringBuilder()
            .AppendLine("## Candidate CV")
            .AppendLine(Truncate(cvText, Ai.MaxCvChars))
            .AppendLine()
            .AppendLine("## Candidate criteria")
            .AppendLine($"Minimum salary: {criteria.MinimumSalary:0} {criteria.Currency}")
            .AppendLine($"Target locations: {(criteria.Cities.Count == 0 ? "any" : string.Join(", ", criteria.Cities))}")
            .AppendLine($"Open to remote: {(criteria.IncludeRemote ? "yes" : "no")}")
            .AppendLine()
            .AppendLine("## Job advert")
            .AppendLine($"Title: {jobTitle}")
            .AppendLine($"Location as advertised: {jobLocation ?? "not stated"}")
            .AppendLine()
            .AppendLine(Truncate(jobDescription, Ai.MaxDescriptionChars))
            .ToString();

        var dto = await AskForJsonAsync<ScoreDto>(
            Prompts.ScoreSystem, prompt, "score", d => d.Score is >= 0 and <= 100, ct);

        if (dto is null) return null;

        return new JobMatchResult
        {
            Score = Math.Clamp(dto.Score!.Value, 0, 100),
            Reasoning = dto.Reasoning?.Trim() ?? string.Empty,
            Strengths = Clean(dto.Strengths),
            Gaps = Clean(dto.Gaps),
            SalaryMin = Sane(dto.SalaryMin),
            SalaryMax = Sane(dto.SalaryMax),
            SalaryCurrency = NormaliseCurrency(dto.SalaryCurrency),
            Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim(),
            IsRemote = dto.IsRemote ?? false,
        };
    }

    public async Task<IReadOnlyList<ExtractedJob>> ExtractJobsAsync(
        string pageText,
        string pageUrl,
        string companyName,
        CancellationToken ct = default)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Company: {companyName}")
            .AppendLine($"Page URL: {pageUrl}")
            .AppendLine()
            .AppendLine("## Page text")
            .AppendLine(Truncate(pageText, Ai.MaxDescriptionChars))
            .ToString();

        var dto = await AskForJsonAsync<ExtractedJobsDto>(
            Prompts.ExtractSystem, prompt, "extract", d => d.Jobs is not null, ct);

        if (dto?.Jobs is null) return [];

        var results = new List<ExtractedJob>(dto.Jobs.Count);
        foreach (var j in dto.Jobs)
        {
            if (string.IsNullOrWhiteSpace(j.Title)) continue;

            var url = ResolveUrl(j.Url, pageUrl);
            if (url is null) continue;

            results.Add(new ExtractedJob
            {
                Title = j.Title.Trim(),
                Location = string.IsNullOrWhiteSpace(j.Location) ? null : j.Location.Trim(),
                Url = url,
                Description = string.IsNullOrWhiteSpace(j.Description) ? null : j.Description.Trim(),
                SalaryMin = Sane(j.SalaryMin),
                SalaryMax = Sane(j.SalaryMax),
                SalaryCurrency = NormaliseCurrency(j.SalaryCurrency),
                IsRemote = j.IsRemote ?? false,
            });
        }

        logger.LogDebug("Extracted {Count} job(s) from {Url}", results.Count, pageUrl);
        return results;
    }

    public async Task<EmailMatchResult?> ClassifyEmailAsync(
        IReadOnlyList<(int Id, string Company, string Title)> openApplications,
        string fromAddress,
        string? subject,
        string? bodySnippet,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder()
            .AppendLine("## Open applications");

        if (openApplications.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var a in openApplications)
                sb.AppendLine($"- id={a.Id} | company={a.Company} | role={a.Title}");
        }

        sb.AppendLine()
            .AppendLine("## Email")
            .AppendLine($"From: {fromAddress}")
            .AppendLine($"Subject: {subject ?? "(none)"}")
            .AppendLine()
            .AppendLine(Truncate(bodySnippet ?? string.Empty, options.Value.Email.BodySnippetChars));

        var validIds = openApplications.Select(a => a.Id).ToHashSet();

        var dto = await AskForJsonAsync<EmailMatchDto>(
            Prompts.EmailSystem, sb.ToString(), "email",
            d => d.Classification is not null &&
                 (d.ApplicationId is null || validIds.Contains(d.ApplicationId.Value)),
            ct);

        if (dto is null) return null;

        var kind = ParseClassification(dto.Classification);

        // A classification without a matched application is meaningless - treat it as unrelated.
        if (dto.ApplicationId is null) kind = EmailClassificationKind.Unrelated;

        return new EmailMatchResult
        {
            ApplicationId = kind == EmailClassificationKind.Unrelated ? null : dto.ApplicationId,
            Classification = kind,
            Confidence = Math.Clamp(dto.Confidence ?? 0, 0, 1),
            Rationale = Truncate(dto.Rationale?.Trim(), 300),
        };
    }

    // -----------------------------------------------------------------------
    // Shared request / validate / retry loop
    // -----------------------------------------------------------------------

    /// <summary>Asks the model, extracts JSON, validates it, and re-asks on failure.
    /// Returns null once the retries are exhausted.</summary>
    private async Task<T?> AskForJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        string operation,
        Func<T, bool> isValid,
        CancellationToken ct) where T : class
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var chatOptions = new ChatOptions { Temperature = 0 };

        var attempts = Math.Max(1, Ai.MaxJsonRetries + 1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Ai.TimeoutSeconds));

            string? replyText;
            try
            {
                var response = await chatClient.GetResponseAsync(messages, chatOptions, timeout.Token);
                replyText = response.Text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("AI {Operation} timed out after {Seconds}s (attempt {Attempt}/{Total})",
                    operation, Ai.TimeoutSeconds, attempt, attempts);
                continue;
            }
            catch (Exception ex)
            {
                // Message only - provider exceptions can echo the request content back.
                logger.LogWarning("AI {Operation} call failed on attempt {Attempt}/{Total}: {Error}",
                    operation, attempt, attempts, ex.Message);
                continue;
            }

            var parsed = JsonExtraction.Deserialise<T>(replyText);

            if (parsed is not null && isValid(parsed))
                return parsed;

            logger.LogWarning("AI {Operation} returned unusable JSON on attempt {Attempt}/{Total}",
                operation, attempt, attempts);

            if (attempt < attempts)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, replyText ?? string.Empty));
                messages.Add(new ChatMessage(ChatRole.User, Prompts.RetryNudge));
            }
        }

        logger.LogError("AI {Operation} gave up after {Total} attempt(s)", operation, attempts);
        return null;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static EmailClassificationKind ParseClassification(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "acknowledged" or "acknowledgement" or "acknowledgment" => EmailClassificationKind.Acknowledged,
            "interview" => EmailClassificationKind.Interview,
            "rejection" or "rejected" => EmailClassificationKind.Rejection,
            "offer" => EmailClassificationKind.Offer,
            _ => EmailClassificationKind.Unrelated,
        };

    /// <summary>Guards against a model returning zero, a negative, or an absurd figure.</summary>
    private static decimal? Sane(decimal? value) =>
        value is > 0 and < 100_000_000m ? value : null;

    private static string? NormaliseCurrency(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static IReadOnlyList<string> Clean(List<string>? items) =>
        items is null
            ? []
            : items.Where(i => !string.IsNullOrWhiteSpace(i))
                   .Select(i => i.Trim())
                   .Take(5)
                   .ToList();

    /// <summary>Turns a possibly relative advert link into an absolute http(s) URL.</summary>
    internal static string? ResolveUrl(string? url, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return pageUrl;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "http" or "https" ? absolute.ToString() : null;

        return Uri.TryCreate(pageUrl, UriKind.Absolute, out var basePage) &&
               Uri.TryCreate(basePage, url, out var combined)
            ? combined.ToString()
            : null;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "\n[...truncated...]";
    }
}

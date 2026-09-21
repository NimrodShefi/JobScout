using System.Net.Http.Json;
using System.Text.Json.Serialization;
using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Boards;

/// <summary>Adzuna search API. Adding another board means one more class like this
/// plus its own options section - nothing else changes.</summary>
public sealed class AdzunaJobBoardProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<AdzunaJobBoardProvider> logger) : IJobBoardProvider
{
    public const string HttpClientName = "adzuna";

    public string Name => "Adzuna";

    private AdzunaOptions Config => options.Value.Boards.Adzuna;

    public bool IsEnabled =>
        Config.Enabled &&
        !string.IsNullOrWhiteSpace(Config.AppId) &&
        !string.IsNullOrWhiteSpace(Config.AppKey);

    public async Task<IReadOnlyList<BoardJobResult>?> SearchAsync(
        BoardSearchRequest request,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            logger.LogDebug("Adzuna is not enabled or not configured - skipping");
            return [];
        }

        var url = BuildUrl(request);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                // The URL carries the credentials, so log the query rather than the URL.
                logger.LogWarning("Adzuna search for '{Query}' in '{City}' returned HTTP {Status}",
                    request.Query, request.City ?? "anywhere", (int)response.StatusCode);
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<AdzunaResponse>(ct);
            var results = payload?.Results ?? [];

            var mapped = results
                .Select(Map)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();

            logger.LogInformation("Adzuna returned {Count} result(s) for '{Query}' in '{City}'",
                mapped.Count, request.Query, request.City ?? "anywhere");

            return mapped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Adzuna search for '{Query}' failed: {Error}", request.Query, ex.Message);

            // Null, not empty: a failed query must not be recorded as "no jobs matched".
            return null;
        }
    }

    /// <summary>Internal so the URL shape can be unit tested without a network call.</summary>
    internal string BuildUrl(BoardSearchRequest request)
    {
        var country = string.IsNullOrWhiteSpace(Config.Country) ? "gb" : Config.Country.Trim().ToLowerInvariant();
        var perPage = Math.Clamp(request.MaxResults, 1, 50);

        var query = new List<string>
        {
            $"app_id={Uri.EscapeDataString(Config.AppId ?? string.Empty)}",
            $"app_key={Uri.EscapeDataString(Config.AppKey ?? string.Empty)}",
            $"results_per_page={perPage}",
            $"what={Uri.EscapeDataString(request.Query)}",
            "content-type=application/json",
        };

        if (!string.IsNullOrWhiteSpace(request.City))
            query.Add($"where={Uri.EscapeDataString(request.City)}");

        if (request.MinimumSalary is > 0)
            query.Add($"salary_min={(long)request.MinimumSalary.Value}");

        // Adverts without a salary must still come back - they are kept and flagged.
        query.Add("salary_include_unknown=1");

        return $"https://api.adzuna.com/v1/api/jobs/{country}/search/1?{string.Join('&', query)}";
    }

    private static BoardJobResult? Map(AdzunaResult r)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.RedirectUrl))
            return null;

        var company = r.Company?.DisplayName;
        if (string.IsNullOrWhiteSpace(company)) return null;

        var location = r.Location?.DisplayName;

        return new BoardJobResult
        {
            CompanyName = company.Trim(),
            Title = r.Title.Trim(),
            Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
            Url = r.RedirectUrl.Trim(),
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            SalaryMin = r.SalaryMin > 0 ? (decimal)r.SalaryMin!.Value : null,
            SalaryMax = r.SalaryMax > 0 ? (decimal)r.SalaryMax!.Value : null,
            SalaryCurrency = null, // Adzuna reports salary in the country's currency.
            PostedAt = r.Created,
            IsRemote = LooksRemote(location) || LooksRemote(r.Title) || LooksRemote(r.Description),
            CompanyBoardUrl = null, // Adzuna has no stable per-company page; the advert link is the way in.
        };
    }

    private static bool LooksRemote(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("work from home", StringComparison.OrdinalIgnoreCase));

    // ---- wire types -------------------------------------------------------

    private sealed class AdzunaResponse
    {
        [JsonPropertyName("results")] public List<AdzunaResult>? Results { get; set; }
    }

    private sealed class AdzunaResult
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("created")] public DateTimeOffset? Created { get; set; }
        [JsonPropertyName("salary_min")] public double? SalaryMin { get; set; }
        [JsonPropertyName("salary_max")] public double? SalaryMax { get; set; }
        [JsonPropertyName("company")] public AdzunaCompany? Company { get; set; }
        [JsonPropertyName("location")] public AdzunaLocation? Location { get; set; }
    }

    private sealed class AdzunaCompany
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    private sealed class AdzunaLocation
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }
}

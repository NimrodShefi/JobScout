using System.Text.Json;
using System.Text.Json.Serialization;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ats;

/// <summary>Ashby posting API. Unlisted postings are skipped - they are reachable by link
/// only and the company has chosen not to advertise them.</summary>
public sealed class AshbyFeed(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<AshbyFeed> logger) : AtsFeedBase(httpClientFactory, options, logger)
{
    public override AtsKind Kind => AtsKind.Ashby;

    internal override string BuildUrl(string token) =>
        $"https://api.ashbyhq.com/posting-api/job-board/{Uri.EscapeDataString(token)}?includeCompensation=true";

    internal override IReadOnlyList<ExtractedJob> Parse(string json)
    {
        var payload = JsonSerializer.Deserialize<Response>(json, Json);

        return (payload?.Jobs ?? [])
            .Where(j => j.IsListed != false)
            .Where(j => !string.IsNullOrWhiteSpace(j.Title) && !string.IsNullOrWhiteSpace(j.JobUrl))
            .Select(j =>
            {
                var location = JoinLocations([j.Location, .. (j.SecondaryLocations ?? []).Select(s => s.Location)]);

                // Only an annual salary with real figures. Ashby reports "not shown" as a
                // component with zeros, which must not read as a £0 salary.
                var salary = j.Compensation?.SummaryComponents?.FirstOrDefault(c =>
                    string.Equals(c.CompensationType, "Salary", StringComparison.OrdinalIgnoreCase) &&
                    c.Interval?.Contains("YEAR", StringComparison.OrdinalIgnoreCase) == true &&
                    (c.MinValue > 0 || c.MaxValue > 0));

                return new ExtractedJob
                {
                    Title = j.Title!.Trim(),
                    Url = j.JobUrl!.Trim(),
                    Location = location,
                    Description = Truncate(j.DescriptionPlain) ?? HtmlToText(j.DescriptionHtml),
                    // workplaceType wins when present: Ashby also sets isRemote on hybrid
                    // roles, and treating a hybrid London job as remote would drop it for
                    // anyone who has remote switched off.
                    IsRemote = string.IsNullOrWhiteSpace(j.WorkplaceType)
                        ? j.IsRemote == true
                        : string.Equals(j.WorkplaceType, "Remote", StringComparison.OrdinalIgnoreCase),
                    SalaryMin = Money(salary?.MinValue),
                    SalaryMax = Money(salary?.MaxValue),
                    SalaryCurrency = salary is null ? null : Blank(salary.CurrencyCode),
                    PostedAt = j.PublishedAt,
                };
            })
            .ToList();
    }

    private sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Job>? Jobs { get; set; }
    }

    private sealed class Job
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("jobUrl")] public string? JobUrl { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("secondaryLocations")] public List<Secondary>? SecondaryLocations { get; set; }
        [JsonPropertyName("isListed")] public bool? IsListed { get; set; }
        [JsonPropertyName("isRemote")] public bool? IsRemote { get; set; }
        [JsonPropertyName("workplaceType")] public string? WorkplaceType { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
        [JsonPropertyName("descriptionHtml")] public string? DescriptionHtml { get; set; }
        [JsonPropertyName("compensation")] public Compensation? Compensation { get; set; }
    }

    private sealed class Secondary
    {
        [JsonPropertyName("location")] public string? Location { get; set; }
    }

    private sealed class Compensation
    {
        [JsonPropertyName("summaryComponents")] public List<Component>? SummaryComponents { get; set; }
    }

    private sealed class Component
    {
        [JsonPropertyName("compensationType")] public string? CompensationType { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
        [JsonPropertyName("currencyCode")] public string? CurrencyCode { get; set; }
        [JsonPropertyName("minValue")] public double? MinValue { get; set; }
        [JsonPropertyName("maxValue")] public double? MaxValue { get; set; }
    }
}

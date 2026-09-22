using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ats;

/// <summary>Greenhouse job board API. <c>content=true</c> adds each advert's description,
/// which arrives as HTML that has itself been HTML-escaped.</summary>
public sealed class GreenhouseFeed(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<GreenhouseFeed> logger) : AtsFeedBase(httpClientFactory, options, logger)
{
    public override AtsKind Kind => AtsKind.Greenhouse;

    internal override string BuildUrl(string token) =>
        $"https://boards-api.greenhouse.io/v1/boards/{Uri.EscapeDataString(token)}/jobs?content=true";

    internal override IReadOnlyList<ExtractedJob> Parse(string json)
    {
        var payload = JsonSerializer.Deserialize<Response>(json, Json);

        return (payload?.Jobs ?? [])
            .Where(j => !string.IsNullOrWhiteSpace(j.Title) && !string.IsNullOrWhiteSpace(j.AbsoluteUrl))
            .Select(j =>
            {
                var location = JoinLocations([j.Location?.Name, .. (j.Offices ?? []).Select(o => o.Name)]);

                return new ExtractedJob
                {
                    Title = j.Title!.Trim(),
                    Url = j.AbsoluteUrl!.Trim(),
                    Location = location,
                    Description = HtmlToText(WebUtility.HtmlDecode(j.Content)),
                    IsRemote = MentionsRemote(location),
                    PostedAt = j.FirstPublished ?? j.UpdatedAt,
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
        [JsonPropertyName("absolute_url")] public string? AbsoluteUrl { get; set; }
        [JsonPropertyName("location")] public Named? Location { get; set; }
        [JsonPropertyName("offices")] public List<Named>? Offices { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("first_published")] public DateTimeOffset? FirstPublished { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class Named
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ats;

/// <summary>Workable's public widget API. <c>details=true</c> adds each description as HTML;
/// without it the adverts arrive bare and scoring would have to fetch every advert page.</summary>
public sealed class WorkableFeed(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<WorkableFeed> logger) : AtsFeedBase(httpClientFactory, options, logger)
{
    public override AtsKind Kind => AtsKind.Workable;

    internal override string BuildUrl(string token) =>
        $"https://apply.workable.com/api/v1/widget/accounts/{Uri.EscapeDataString(token)}?details=true";

    internal override IReadOnlyList<ExtractedJob> Parse(string json)
    {
        var payload = JsonSerializer.Deserialize<Response>(json, Json);

        // Workable lists a multi-office advert once per office, every copy with the same URL.
        // They are folded back into one advert naming every office - otherwise the URL dedupe
        // keeps only the first copy, and a London role listed Manchester-first would be
        // filtered out as not in London.
        return (payload?.Jobs ?? [])
            .Where(j => !string.IsNullOrWhiteSpace(j.Title) && !string.IsNullOrWhiteSpace(j.Url ?? j.Shortlink))
            .GroupBy(j => (j.Url ?? j.Shortlink)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var j = g.First();

                var location = JoinLocations(g.SelectMany(copy =>
                    (copy.Locations ?? []).Where(l => l.Hidden != true).Select(l => Place(l.City, l.Country))
                        .Append(Place(copy.City, copy.Country))));

                return new ExtractedJob
                {
                    Title = j.Title!.Trim(),
                    Url = g.Key,
                    Location = location,
                    Description = HtmlToText(j.Description),
                    IsRemote = g.Any(copy => copy.Telecommuting == true) || MentionsRemote(location),
                    PostedAt = ParseDate(j.PublishedOn) ?? ParseDate(j.CreatedAt),
                };
            })
            .ToList();
    }

    private static string? Place(string? city, string? country) =>
        (Blank(city), Blank(country)) switch
        {
            ({ } c, { } k) => $"{c}, {k}",
            ({ } c, null) => c,
            (null, { } k) => k,
            _ => null,
        };

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    private sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Job>? Jobs { get; set; }
    }

    private sealed class Job
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("shortlink")] public string? Shortlink { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("locations")] public List<JobLocation>? Locations { get; set; }
        [JsonPropertyName("telecommuting")] public bool? Telecommuting { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("published_on")] public string? PublishedOn { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    }

    private sealed class JobLocation
    {
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("hidden")] public bool? Hidden { get; set; }
    }
}

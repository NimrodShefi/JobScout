using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ats;

/// <summary>Lever postings API. The description is split across an intro, several bullet
/// lists (requirements, responsibilities) and a closing section, so they are stitched back
/// together - the lists are where most of the substance is.</summary>
public sealed class LeverFeed(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger<LeverFeed> logger) : AtsFeedBase(httpClientFactory, options, logger)
{
    public override AtsKind Kind => AtsKind.Lever;

    internal override string BuildUrl(string token) =>
        $"https://api.lever.co/v0/postings/{Uri.EscapeDataString(token)}?mode=json";

    internal override IReadOnlyList<ExtractedJob> Parse(string json)
    {
        var postings = JsonSerializer.Deserialize<List<Posting>>(json, Json) ?? [];

        return postings
            .Where(p => !string.IsNullOrWhiteSpace(p.Text) && !string.IsNullOrWhiteSpace(p.HostedUrl))
            .Select(p =>
            {
                var location = JoinLocations([p.Categories?.Location, .. (p.Categories?.AllLocations ?? [])]);
                var yearly = p.SalaryRange?.Interval?.Contains("year", StringComparison.OrdinalIgnoreCase) == true;

                return new ExtractedJob
                {
                    Title = p.Text!.Trim(),
                    Url = p.HostedUrl!.Trim(),
                    Location = location,
                    Description = Describe(p),
                    IsRemote = string.Equals(p.WorkplaceType, "remote", StringComparison.OrdinalIgnoreCase) ||
                               MentionsRemote(location),
                    SalaryMin = yearly ? Money(p.SalaryRange!.Min) : null,
                    SalaryMax = yearly ? Money(p.SalaryRange!.Max) : null,
                    SalaryCurrency = yearly ? Blank(p.SalaryRange!.Currency) : null,
                    PostedAt = p.CreatedAt is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedAt.Value) : null,
                };
            })
            .ToList();
    }

    private string? Describe(Posting p)
    {
        var sb = new StringBuilder();

        if (Blank(p.DescriptionPlain) is { } intro) sb.AppendLine(intro).AppendLine();

        foreach (var list in p.Lists ?? [])
        {
            if (Blank(list.Text) is { } heading) sb.AppendLine(heading);
            if (HtmlToText(list.Content) is { } items) sb.AppendLine(items);
            sb.AppendLine();
        }

        if (Blank(p.AdditionalPlain) is { } closing) sb.AppendLine(closing);

        return Truncate(sb.ToString());
    }

    private sealed class Posting
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("hostedUrl")] public string? HostedUrl { get; set; }
        [JsonPropertyName("categories")] public Categories? Categories { get; set; }
        [JsonPropertyName("createdAt")] public long? CreatedAt { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
        [JsonPropertyName("additionalPlain")] public string? AdditionalPlain { get; set; }
        [JsonPropertyName("lists")] public List<Section>? Lists { get; set; }
        [JsonPropertyName("workplaceType")] public string? WorkplaceType { get; set; }
        [JsonPropertyName("salaryRange")] public Salary? SalaryRange { get; set; }
    }

    private sealed class Categories
    {
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("allLocations")] public List<string>? AllLocations { get; set; }
    }

    private sealed class Section
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class Salary
    {
        [JsonPropertyName("min")] public double? Min { get; set; }
        [JsonPropertyName("max")] public double? Max { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
    }
}

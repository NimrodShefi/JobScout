using System.Text.Json.Serialization;

namespace JobScout.Infrastructure.Ai;

/// <summary>Wire shapes the model is asked to produce. Deliberately separate from the
/// Core models so a malformed reply cannot put a half-built domain object into play.</summary>
internal sealed class ScoreDto
{
    [JsonPropertyName("score")] public int? Score { get; set; }
    [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    [JsonPropertyName("strengths")] public List<string>? Strengths { get; set; }
    [JsonPropertyName("gaps")] public List<string>? Gaps { get; set; }
    [JsonPropertyName("salary_min")] public decimal? SalaryMin { get; set; }
    [JsonPropertyName("salary_max")] public decimal? SalaryMax { get; set; }
    [JsonPropertyName("salary_currency")] public string? SalaryCurrency { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("is_remote")] public bool? IsRemote { get; set; }
}

internal sealed class ExtractedJobsDto
{
    [JsonPropertyName("jobs")] public List<ExtractedJobDto>? Jobs { get; set; }
}

internal sealed class ExtractedJobDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("salary_min")] public decimal? SalaryMin { get; set; }
    [JsonPropertyName("salary_max")] public decimal? SalaryMax { get; set; }
    [JsonPropertyName("salary_currency")] public string? SalaryCurrency { get; set; }
    [JsonPropertyName("is_remote")] public bool? IsRemote { get; set; }
}

internal sealed class EmailMatchDto
{
    [JsonPropertyName("application_id")] public int? ApplicationId { get; set; }
    [JsonPropertyName("classification")] public string? Classification { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    [JsonPropertyName("rationale")] public string? Rationale { get; set; }
}

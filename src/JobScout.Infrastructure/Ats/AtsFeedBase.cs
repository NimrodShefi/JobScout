using System.Net;
using System.Text.Json;
using JobScout.Core.Abstractions;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Infrastructure.Fetching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Ats;

/// <summary>The HTTP side every feed shares: one GET, JSON back, null on any failure. Each
/// service only supplies its URL and its parser.
///
/// These are published APIs meant for programmatic use, so they are called directly - the
/// same stance as the Adzuna board - rather than through the careers-page fetcher and its
/// robots.txt gate.</summary>
public abstract class AtsFeedBase(
    IHttpClientFactory httpClientFactory,
    IOptions<JobScoutOptions> options,
    ILogger logger) : IAtsFeed
{
    public const string HttpClientName = "ats";

    public abstract AtsKind Kind { get; }

    protected int MaxDescriptionChars => Math.Max(500, options.Value.Ats.MaxDescriptionChars);

    /// <summary>Internal so the URL shape can be unit tested.</summary>
    internal abstract string BuildUrl(string token);

    internal abstract IReadOnlyList<ExtractedJob> Parse(string json);

    public async Task<IReadOnlyList<ExtractedJob>?> FetchAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(BuildUrl(token.Trim()), ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The normal answer when probing a guessed token, so not worth a warning.
                logger.LogDebug("{Kind} has no board '{Token}'", Kind, token);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Kind} board '{Token}' returned HTTP {Status}", Kind, token, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var jobs = Parse(json);

            logger.LogDebug("{Kind} board '{Token}': {Count} advert(s)", Kind, token, jobs.Count);
            return jobs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Reading the {Kind} board '{Token}' failed: {Error}", Kind, token, ex.Message);
            return null;
        }
    }

    // ---- helpers shared by the parsers ----------------------------------

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    protected string? HtmlToText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Blank(HtmlText.ExtractText(html, MaxDescriptionChars));

    protected string? Truncate(string? text) =>
        Blank(text) is { } t ? (t.Length <= MaxDescriptionChars ? t : t[..MaxDescriptionChars]) : null;

    protected static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Joins location parts, dropping blanks and repeats, so every city an advert
    /// names is visible to the location filter.</summary>
    protected static string? JoinLocations(IEnumerable<string?> parts)
    {
        var distinct = parts
            .Select(Blank)
            .Where(p => p is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 0 ? null : string.Join("; ", distinct);
    }

    protected static bool MentionsRemote(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("work from home", StringComparison.OrdinalIgnoreCase));

    /// <summary>Zero or negative salary figures mean "not stated" in these feeds.</summary>
    protected static decimal? Money(double? value) => value is > 0 ? (decimal)value.Value : null;
}

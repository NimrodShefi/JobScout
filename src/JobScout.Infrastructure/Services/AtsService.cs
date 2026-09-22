using JobScout.Core.Abstractions;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Core.Services;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Services;

/// <summary>A confirmed job-board feed, with the adverts read while confirming it so the
/// caller does not have to fetch them a second time.</summary>
public sealed record AtsDetection(AtsKind Kind, string Token, bool Guessed, IReadOnlyList<ExtractedJob> Jobs);

/// <summary>Finds and reads companies' job-board feeds.
///
/// A candidate is only trusted once its live feed answers. How much it must prove depends on
/// where it came from: a board named by the careers URL or linked from the careers page is
/// the company's own, so an empty board still counts; a token guessed from the name could
/// belong to anyone, so it must at least have adverts on it, and the run summary flags it
/// for me to check.</summary>
public sealed class AtsService(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IEnumerable<IAtsFeed> feeds,
    IOptions<JobScoutOptions> options,
    ILogger<AtsService> logger)
{
    private readonly Dictionary<AtsKind, IAtsFeed> _feeds = feeds.ToDictionary(f => f.Kind);

    public AtsOptions Config => options.Value.Ats;

    public bool Enabled => Config.Enabled && _feeds.Count > 0;

    /// <summary>Reads one board. Null when the board could not be read.</summary>
    public Task<IReadOnlyList<ExtractedJob>?> FetchAsync(AtsKind kind, string token, CancellationToken ct = default) =>
        _feeds.TryGetValue(kind, out var feed)
            ? feed.FetchAsync(token, ct)
            : Task.FromResult<IReadOnlyList<ExtractedJob>?>(null);

    /// <summary>Looks for a feed from what is known about a company: its careers URL first,
    /// then tokens guessed from its name.</summary>
    public async Task<AtsDetection?> DetectAsync(string companyName, string? careersUrl, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        if (AtsLocator.TryParseUrl(careersUrl) is { } named &&
            await ConfirmAsync(named, guessed: false, ct) is { } fromUrl)
        {
            return fromUrl;
        }

        foreach (var token in AtsLocator.GuessTokens(companyName))
        {
            foreach (var kind in _feeds.Keys.Order())
            {
                if (await ConfirmAsync(new AtsReference(kind, token), guessed: true, ct) is { } fromGuess)
                    return fromGuess;
            }
        }

        return null;
    }

    /// <summary>Looks for a feed linked or embedded in a careers page's HTML. Costs nothing
    /// unless the page names a board.</summary>
    public async Task<AtsDetection?> DetectFromHtmlAsync(string? html, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        // A page naming several boards is rare; the first one that answers wins.
        foreach (var candidate in AtsLocator.FindInHtml(html).Take(3))
        {
            if (await ConfirmAsync(candidate, guessed: false, ct) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Records the outcome of a detection attempt. A found feed is saved; either way
    /// the check time is stamped so a company without one is not probed again tomorrow.
    /// A feed I have already set by hand is never overwritten.</summary>
    public async Task SaveDetectionAsync(int companyId, AtsDetection? detection, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return;

        company.AtsCheckedAt = DateTimeOffset.UtcNow;

        if (detection is not null && !company.HasAtsFeed)
        {
            company.AtsKind = detection.Kind;
            company.AtsToken = detection.Token;

            logger.LogInformation("{Company} publishes its adverts on {Kind} as '{Token}'{How}",
                company.Name, detection.Kind, detection.Token, detection.Guessed ? " (guessed from the name)" : "");
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<AtsDetection?> ConfirmAsync(AtsReference candidate, bool guessed, CancellationToken ct)
    {
        var jobs = await FetchAsync(candidate.Kind, candidate.Token, ct);

        if (jobs is null) return null;
        if (guessed && jobs.Count == 0) return null;

        return new AtsDetection(candidate.Kind, candidate.Token, guessed, jobs);
    }
}

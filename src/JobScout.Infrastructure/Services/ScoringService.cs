using System.Security.Cryptography;
using System.Text;
using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Services;

public sealed record ScoringOutcome
{
    public int Scored { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int DescriptionsFetched { get; init; }
    public bool HitCap { get; init; }

    /// <summary>Nothing was scored because no CV has been uploaded. Not a failure, but the
    /// run did far less than it looks like it did, so the caller flags it.</summary>
    public bool NoCv { get; init; }
}

/// <summary>Scores listings belonging to USE companies against the CV. Board listings often
/// arrive with a thin description, so the advert page is fetched first when needed. Unchanged
/// listings are skipped via a content hash, and the number of AI calls per run is capped.</summary>
public sealed class ScoringService(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IJobMatcher matcher,
    IPageFetcher pageFetcher,
    IOptions<JobScoutOptions> options,
    ILogger<ScoringService> logger)
{
    /// <summary>Scores every new or unscored listing for USE companies, up to the configured cap.
    /// <paramref name="onlyCompanyId"/> limits the pass to one company, which is what happens
    /// when I flip a company to USE.</summary>
    public async Task<ScoringOutcome> ScorePendingAsync(
        int? onlyCompanyId = null,
        CancellationToken ct = default)
    {
        var config = await LoadConfigAsync(ct);

        if (string.IsNullOrWhiteSpace(config.CvText))
        {
            logger.LogWarning("No CV text has been uploaded - scoring skipped");
            return new ScoringOutcome { NoCv = true };
        }

        var criteria = ToCriteria(config);
        var cap = Math.Max(1, options.Value.Scoring.MaxListingsPerRun);

        var pending = await LoadPendingAsync(onlyCompanyId, cap, ct);

        if (pending.Count == 0)
            return new ScoringOutcome();

        logger.LogInformation("Scoring {Count} listing(s){Scope}", pending.Count,
            onlyCompanyId is null ? "" : $" for company {onlyCompanyId}");

        var scored = 0;
        var skipped = 0;
        var failed = 0;
        var fetched = 0;

        var gate = new SemaphoreSlim(Math.Max(1, options.Value.Scoring.MaxConcurrency));

        var tasks = pending.Select(async listing =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await ScoreOneAsync(listing, config.CvText!, criteria, ct);

                switch (result.Kind)
                {
                    case ScoreKind.Scored: Interlocked.Increment(ref scored); break;
                    case ScoreKind.Skipped: Interlocked.Increment(ref skipped); break;
                    default: Interlocked.Increment(ref failed); break;
                }

                if (result.FetchedDescription) Interlocked.Increment(ref fetched);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        gate.Dispose();

        // Whether more work is waiting is worth knowing when the cap is doing real work.
        var hitCap = pending.Count >= cap;

        if (hitCap)
            logger.LogInformation("Reached the per-run scoring cap of {Cap}; remaining listings wait for the next run", cap);

        return new ScoringOutcome
        {
            Scored = scored,
            Skipped = skipped,
            Failed = failed,
            DescriptionsFetched = fetched,
            HitCap = hitCap,
        };
    }

    private enum ScoreKind { Scored, Skipped, Failed }

    private readonly record struct ScoreOutcome(ScoreKind Kind, bool FetchedDescription);

    private async Task<ScoreOutcome> ScoreOneAsync(
        PendingListing pending,
        string cvText,
        MatchCriteria criteria,
        CancellationToken ct)
    {
        var description = pending.Description;
        var fetchedDescription = false;

        // Board adverts usually carry a teaser only; go to the advert page for the real text.
        if (NeedsFullDescription(description))
        {
            var page = await pageFetcher.FetchAsync(pending.Url, ct);

            if (page.Success && !string.IsNullOrWhiteSpace(page.Text))
            {
                description = page.Text;
                fetchedDescription = true;
                await SaveDescriptionAsync(pending.Id, page.Text, ct);
            }
            else
            {
                logger.LogDebug("Could not fetch a fuller description for listing {Id}: {Error}",
                    pending.Id, page.Error ?? "no text");
            }
        }

        if (string.IsNullOrWhiteSpace(description))
            description = pending.Title;

        var hash = ContentHash(pending.Title, pending.Location, description);

        if (hash == pending.ScoredContentHash && pending.Score is not null)
        {
            logger.LogDebug("Listing {Id} is unchanged since it was scored - skipping", pending.Id);
            return new ScoreOutcome(ScoreKind.Skipped, fetchedDescription);
        }

        var match = await matcher.ScoreAsync(cvText, criteria, pending.Title, pending.Location, description, ct);

        if (match is null)
        {
            logger.LogWarning("Scoring failed for listing {Id}", pending.Id);
            return new ScoreOutcome(ScoreKind.Failed, fetchedDescription);
        }

        await SaveScoreAsync(pending.Id, match, hash, ct);
        return new ScoreOutcome(ScoreKind.Scored, fetchedDescription);
    }

    /// <summary>A couple of sentences is a teaser, not a job description.</summary>
    private static bool NeedsFullDescription(string? description) =>
        (description?.Trim().Length ?? 0) < 400;

    /// <summary>The startup initialiser always creates the settings row, but a run must not
    /// die with an obscure error if it is somehow missing - defaults are a better failure.</summary>
    private async Task<AppConfig> LoadConfigAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppConfig();
    }

    public static MatchCriteria ToCriteria(AppConfig config) => new()
    {
        MinimumSalary = config.MinimumSalary,
        Currency = config.Currency,
        Cities = config.CityList,
        IncludeRemote = config.IncludeRemote,
        DesiredRoles = config.DesiredRoleList,
        ExcludedRoles = config.ExcludedRoleList,
    };

    private async Task<List<PendingListing>> LoadPendingAsync(int? onlyCompanyId, int cap, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.JobListings
            .AsNoTracking()
            .Where(l => l.Company!.Status == CompanyStatus.USE)
            .Where(l => l.Status == JobListingStatus.New || l.Score == null);

        if (onlyCompanyId is not null)
            query = query.Where(l => l.CompanyId == onlyCompanyId.Value);

        return await query
            .OrderByDescending(l => l.FoundAt)
            .Take(cap)
            .Select(l => new PendingListing(
                l.Id, l.Title, l.Location, l.Url, l.Description, l.Score, l.ScoredContentHash))
            .ToListAsync(ct);
    }

    private async Task SaveDescriptionAsync(int listingId, string description, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var listing = await db.JobListings.FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null) return;

        listing.Description = description;
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveScoreAsync(int listingId, JobMatchResult match, string hash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var listing = await db.JobListings.FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null) return;

        listing.Score = match.Score;
        listing.Reasoning = match.Reasoning;
        listing.Strengths = match.Strengths.Count > 0 ? string.Join('\n', match.Strengths) : null;
        listing.Gaps = match.Gaps.Count > 0 ? string.Join('\n', match.Gaps) : null;
        listing.ScoredAt = DateTimeOffset.UtcNow;
        listing.ScoredContentHash = hash;

        // The AI often reads a salary off the advert that the source did not give us.
        if (!listing.SalaryKnown && (match.SalaryMin is not null || match.SalaryMax is not null))
        {
            listing.SalaryMin = match.SalaryMin;
            listing.SalaryMax = match.SalaryMax;
            listing.SalaryCurrency = match.SalaryCurrency ?? listing.SalaryCurrency;
            listing.SalaryKnown = true;
        }

        if (string.IsNullOrWhiteSpace(listing.Location) && !string.IsNullOrWhiteSpace(match.Location))
            listing.Location = match.Location;

        if (match.IsRemote) listing.IsRemote = true;

        // Leave Dismissed and Applied alone - scoring is not a reason to reopen them.
        if (listing.Status == JobListingStatus.New)
            listing.Status = JobListingStatus.Scored;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Identifies the content that was scored, so an unchanged advert is not paid for twice.</summary>
    public static string ContentHash(string title, string? location, string? description)
    {
        var payload = $"{title}␟{location}␟{description}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(bytes);
    }

    private readonly record struct PendingListing(
        int Id,
        string Title,
        string? Location,
        string Url,
        string? Description,
        int? Score,
        string? ScoredContentHash);
}

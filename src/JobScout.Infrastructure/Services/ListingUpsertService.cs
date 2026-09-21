using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Services;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobScout.Infrastructure.Services;

/// <summary>What one upsert batch did, for the run summary.</summary>
public sealed record UpsertOutcome
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int Duplicates { get; init; }
    public int RejectedLocation { get; init; }
    public int RejectedSalary { get; init; }
    public int RejectedExcludedRole { get; init; }
    public int RejectedUnwantedRole { get; init; }

    /// <summary>Everything dropped by a rule rather than saved.</summary>
    public int RejectedTotal =>
        RejectedLocation + RejectedSalary + RejectedExcludedRole + RejectedUnwantedRole;

    public int Considered => Inserted + Updated + Duplicates + RejectedTotal;

    public static UpsertOutcome operator +(UpsertOutcome a, UpsertOutcome b) => new()
    {
        Inserted = a.Inserted + b.Inserted,
        Updated = a.Updated + b.Updated,
        Duplicates = a.Duplicates + b.Duplicates,
        RejectedLocation = a.RejectedLocation + b.RejectedLocation,
        RejectedSalary = a.RejectedSalary + b.RejectedSalary,
        RejectedExcludedRole = a.RejectedExcludedRole + b.RejectedExcludedRole,
        RejectedUnwantedRole = a.RejectedUnwantedRole + b.RejectedUnwantedRole,
    };
}

/// <summary>Applies the salary and location filters, then upserts listings by URL.
/// The URL is the dedupe key, so re-running a scan never creates a second row for an
/// advert we already hold.</summary>
public sealed class ListingUpsertService(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    ILogger<ListingUpsertService> logger)
{
    /// <summary>Upserts listings for a company that already exists.</summary>
    public async Task<UpsertOutcome> UpsertForCompanyAsync(
        int companyId,
        string source,
        IEnumerable<ExtractedJob> jobs,
        MatchCriteria criteria,
        CancellationToken ct = default)
    {
        var candidates = jobs.Select(j => new Candidate(
            j.Title, j.Location, j.Url, j.Description,
            j.SalaryMin, j.SalaryMax, j.SalaryCurrency, j.IsRemote, PostedAt: null));

        return await UpsertAsync(companyId, source, candidates, criteria, ct);
    }

    /// <summary>Upserts board results: creates any unseen company as REVIEW with a BoardUrl
    /// and no careers page, then saves its listings unscored.
    ///
    /// <paramref name="maxNewCompanies"/> caps how many unseen companies this batch may add;
    /// null means no cap. The cap has to bite here and not only between queries, because a
    /// single board query routinely returns adverts from dozens of companies we have never
    /// seen. Companies already on file are unaffected - their adverts keep flowing however
    /// much of the budget is left.</summary>
    public async Task<(UpsertOutcome Listings, int NewCompanies)> UpsertBoardResultsAsync(
        string boardName,
        IEnumerable<BoardJobResult> results,
        MatchCriteria criteria,
        int? maxNewCompanies = null,
        CancellationToken ct = default)
    {
        var byCompany = results
            .Where(r => !string.IsNullOrWhiteSpace(r.CompanyName))
            .GroupBy(r => CompanyNameNormaliser.Normalise(r.CompanyName))
            .Where(g => g.Key.Length > 0);

        var total = new UpsertOutcome();
        var newCompanies = 0;

        foreach (var group in byCompany)
        {
            ct.ThrowIfCancellationRequested();

            var first = group.First();
            var mayCreate = maxNewCompanies is null || newCompanies < maxNewCompanies;

            var (companyId, created) = await EnsureCompanyAsync(
                first.CompanyName, group.Key, boardName, first.CompanyBoardUrl ?? first.Url,
                mayCreate, ct);

            // Out of budget and we have never seen this company: skip its adverts too, since
            // a listing with no company row has nowhere to hang. The next run will find them.
            if (companyId is null) continue;

            if (created) newCompanies++;

            var candidates = group.Select(r => new Candidate(
                r.Title, r.Location, r.Url, r.Description,
                r.SalaryMin, r.SalaryMax, r.SalaryCurrency, r.IsRemote, r.PostedAt));

            total += await UpsertAsync(companyId.Value, boardName, candidates, criteria, ct);
        }

        return (total, newCompanies);
    }

    /// <summary>Finds a company by normalised name, or creates it as REVIEW.
    /// An existing company keeps its status and its careers URL - discovery never
    /// overwrites a decision already made.
    ///
    /// Returns a null id when the company is unknown and <paramref name="allowCreate"/> is
    /// false, which is how the per-run discovery cap is enforced.</summary>
    public async Task<(int? CompanyId, bool Created)> EnsureCompanyAsync(
        string displayName,
        string normalisedName,
        string source,
        string? boardUrl,
        bool allowCreate = true,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Companies
            .FirstOrDefaultAsync(c => c.NormalisedName == normalisedName, ct);

        if (existing is not null)
        {
            // Fill in a board link if we did not have one, but leave everything else alone.
            if (string.IsNullOrWhiteSpace(existing.BoardUrl) && !string.IsNullOrWhiteSpace(boardUrl))
            {
                existing.BoardUrl = boardUrl;
                await db.SaveChangesAsync(ct);
            }

            return (existing.Id, false);
        }

        if (!allowCreate) return (null, false);

        var company = new Company
        {
            Name = displayName.Trim(),
            NormalisedName = normalisedName,
            Source = source,
            Status = CompanyStatus.REVIEW,
            Url = null,
            BoardUrl = boardUrl,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };

        db.Companies.Add(company);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Discovered new company {Company} via {Source} - marked REVIEW",
                company.Name, source);
            return (company.Id, true);
        }
        catch (DbUpdateException)
        {
            // Another run inserted the same company between the read and the write.
            db.ChangeTracker.Clear();
            var raced = await db.Companies.FirstAsync(c => c.NormalisedName == normalisedName, ct);
            return (raced.Id, false);
        }
    }

    private async Task<UpsertOutcome> UpsertAsync(
        int companyId,
        string source,
        IEnumerable<Candidate> candidates,
        MatchCriteria criteria,
        CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var duplicates = 0;
        var rejectedLocation = 0;
        var rejectedSalary = 0;
        var rejectedExcludedRole = 0;
        var rejectedUnwantedRole = 0;

        // Dedupe within the batch first - a careers page often lists the same advert twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(c.Url) || string.IsNullOrWhiteSpace(c.Title))
                continue;

            var url = NormaliseUrl(c.Url);

            if (!seen.Add(url))
            {
                duplicates++;
                continue;
            }

            var outcome = ListingFilter.Evaluate(
                c.Title, c.Location, c.IsRemote, c.SalaryMin, c.SalaryMax, criteria);

            if (outcome == FilterOutcome.RejectedExcludedRole)
            {
                logger.LogDebug("Dropped '{Title}': matched the excluded role term '{Term}'",
                    c.Title, RoleMatcher.FirstMatch(c.Title, criteria.ExcludedRoles));
                rejectedExcludedRole++;
                continue;
            }

            if (outcome == FilterOutcome.RejectedUnwantedRole)
            {
                logger.LogDebug("Dropped '{Title}': matches none of the roles I am looking for", c.Title);
                rejectedUnwantedRole++;
                continue;
            }

            if (outcome == FilterOutcome.RejectedLocation) { rejectedLocation++; continue; }
            if (outcome == FilterOutcome.RejectedSalary) { rejectedSalary++; continue; }

            var existing = await db.JobListings.FirstOrDefaultAsync(l => l.Url == url, ct);

            if (existing is null)
            {
                db.JobListings.Add(new JobListing
                {
                    CompanyId = companyId,
                    Title = c.Title.Trim(),
                    Location = c.Location?.Trim(),
                    Url = url,
                    Description = c.Description,
                    SalaryMin = c.SalaryMin,
                    SalaryMax = c.SalaryMax,
                    SalaryCurrency = c.SalaryCurrency ?? criteria.Currency,
                    SalaryKnown = ListingFilter.IsSalaryKnown(c.SalaryMin, c.SalaryMax),
                    Source = source,
                    FoundAt = DateTimeOffset.UtcNow,
                    PostedAt = c.PostedAt,
                    IsRemote = c.IsRemote || ListingFilter.LooksRemote(c.Location),
                    Status = JobListingStatus.New,
                });

                inserted++;
            }
            else if (UpdateInPlace(existing, c))
            {
                updated++;
            }
            else
            {
                duplicates++;
            }
        }

        if (inserted + updated > 0)
            await db.SaveChangesAsync(ct);

        return new UpsertOutcome
        {
            Inserted = inserted,
            Updated = updated,
            Duplicates = duplicates,
            RejectedLocation = rejectedLocation,
            RejectedSalary = rejectedSalary,
            RejectedExcludedRole = rejectedExcludedRole,
            RejectedUnwantedRole = rejectedUnwantedRole,
        };
    }

    /// <summary>Fills in details we did not have before. Returns true if anything changed.
    /// A listing I have already dismissed or applied to is left alone.</summary>
    private static bool UpdateInPlace(JobListing existing, Candidate c)
    {
        if (existing.Status is JobListingStatus.Dismissed or JobListingStatus.Applied)
            return false;

        var changed = false;

        if (string.IsNullOrWhiteSpace(existing.Description) && !string.IsNullOrWhiteSpace(c.Description))
        {
            existing.Description = c.Description;
            changed = true;
        }

        if (!existing.SalaryKnown && ListingFilter.IsSalaryKnown(c.SalaryMin, c.SalaryMax))
        {
            existing.SalaryMin = c.SalaryMin;
            existing.SalaryMax = c.SalaryMax;
            if (!string.IsNullOrWhiteSpace(c.SalaryCurrency)) existing.SalaryCurrency = c.SalaryCurrency;
            existing.SalaryKnown = true;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(existing.Location) && !string.IsNullOrWhiteSpace(c.Location))
        {
            existing.Location = c.Location.Trim();
            changed = true;
        }

        return changed;
    }

    /// <summary>Drops the fragment and trailing slash and lower-cases the host, so the same
    /// advert reached by slightly different links collapses onto one row. Query strings are
    /// kept - many boards put the advert id there.</summary>
    public static string NormaliseUrl(string url)
    {
        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        var builder = new UriBuilder(uri) { Fragment = string.Empty };

        if (builder.Path.Length > 1 && builder.Path.EndsWith('/'))
            builder.Path = builder.Path.TrimEnd('/');

        builder.Host = builder.Host.ToLowerInvariant();

        // Drop the default port so http://x:80/y and http://x/y match.
        if ((builder.Scheme == "http" && builder.Port == 80) ||
            (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.ToString();
    }

    /// <summary>One advert, whatever produced it.</summary>
    private readonly record struct Candidate(
        string Title,
        string? Location,
        string Url,
        string? Description,
        decimal? SalaryMin,
        decimal? SalaryMax,
        string? SalaryCurrency,
        bool IsRemote,
        DateTimeOffset? PostedAt);
}

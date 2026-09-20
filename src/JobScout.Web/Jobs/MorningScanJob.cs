using System.Text;
using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Web.Jobs;

/// <summary>Morning scan: read every USE company's careers page, extract the adverts with
/// the AI, filter by city and salary, upsert by URL, then score everything new or unscored
/// for USE companies - including board-sourced listings, whose description gets fetched
/// from the advert page when it is too thin to judge.</summary>
public sealed class MorningScanJob(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IPageFetcher pageFetcher,
    IJobMatcher matcher,
    ListingUpsertService upsert,
    ScoringService scoring,
    ILogger<MorningScanJob> logger) : IScheduledJob
{
    public const string Key = "morning-scan";


    public async Task<string> RunAsync(CancellationToken ct)
    {
        var companies = await LoadUseCompaniesAsync(ct);
        var criteria = await LoadCriteriaAsync(ct);

        var scanned = 0;
        var failed = 0;
        var blocked = 0;
        var refused = 0;
        var refusedCompanies = new List<string>();
        var totals = new UpsertOutcome();

        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            var page = await pageFetcher.FetchAsync(company.Url, ct);

            if (page.BlockedByRobots)
            {
                logger.LogInformation("Skipping {Company}: robots.txt disallows {Url}", company.Name, company.Url);
                blocked++;
                continue;
            }

            if (page.BlockedByBotProtection)
            {
                // Bot protection, not a broken link. Worth calling out separately so it is
                // obvious this needs the browser or the job-board route rather than a retry.
                logger.LogWarning(
                    "{Company} refused the request with HTTP {Status}. The site blocks automated " +
                    "clients; install the Playwright browsers, or let discovery pick this company " +
                    "up from a job board instead.",
                    company.Name, page.StatusCode);

                refused++;
                refusedCompanies.Add(company.Name);
                continue;
            }

            if (!page.Success || string.IsNullOrWhiteSpace(page.Text))
            {
                logger.LogWarning("Could not read the careers page for {Company}: {Error}",
                    company.Name, page.Error ?? "no text returned");
                failed++;
                continue;
            }

            var extracted = await matcher.ExtractJobsAsync(page.Text, company.Url, company.Name, ct);

            var outcome = await upsert.UpsertForCompanyAsync(
                company.Id, "CareerPage", extracted, criteria, ct);

            totals += outcome;
            scanned++;

            await MarkCheckedAsync(company.Id, ct);

            logger.LogInformation(
                "{Company}: {Extracted} advert(s) found, {Inserted} new, {Updated} updated, {Rejected} filtered out",
                company.Name, extracted.Count, outcome.Inserted, outcome.Updated,
                outcome.RejectedLocation + outcome.RejectedSalary);
        }

        // Score everything pending, board-sourced listings included.
        var scored = await scoring.ScorePendingAsync(ct: ct);

        return Summarise(companies.Count, scanned, failed, blocked, refused, refusedCompanies, totals, scored);
    }

    private async Task<JobScout.Core.Models.MatchCriteria> LoadCriteriaAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppConfig();
        return ScoringService.ToCriteria(config);
    }

    private static string Summarise(
        int companyCount, int scanned, int failed, int blocked, int refused,
        List<string> refusedCompanies, UpsertOutcome totals, ScoringOutcome scored)
    {
        var sb = new StringBuilder()
            .Append($"{scanned}/{companyCount} careers page(s) read");

        if (failed > 0) sb.Append($", {failed} failed");
        if (blocked > 0) sb.Append($", {blocked} blocked by robots.txt");

        if (refused > 0)
        {
            sb.Append($", {refused} refused automated access ({string.Join(", ", refusedCompanies.Take(3))}")
              .Append(refusedCompanies.Count > 3 ? $" and {refusedCompanies.Count - 3} more)" : ")");
        }

        sb.Append($"; {totals.Inserted} new listing(s), {totals.Updated} updated");

        var filtered = totals.RejectedLocation + totals.RejectedSalary;
        if (filtered > 0)
            sb.Append($", {filtered} filtered out ({totals.RejectedLocation} location, {totals.RejectedSalary} salary)");

        sb.Append($"; {scored.Scored} scored");

        if (scored.Skipped > 0) sb.Append($", {scored.Skipped} unchanged");
        if (scored.Failed > 0) sb.Append($", {scored.Failed} scoring failure(s)");
        if (scored.HitCap) sb.Append(" (per-run cap reached)");

        if (refused > 0)
        {
            sb.Append(". Sites that refuse automated access need the Playwright browsers installed, ")
              .Append("or are better reached through a job board on the Discovery run.");
        }

        return sb.ToString();
    }

    private async Task<List<CompanyRef>> LoadUseCompaniesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Companies
            .AsNoTracking()
            .Where(c => c.Status == CompanyStatus.USE && c.Url != null && c.Url != "")
            .OrderBy(c => c.Name)
            .Select(c => new CompanyRef(c.Id, c.Name, c.Url!))
            .ToListAsync(ct);
    }

    private async Task MarkCheckedAsync(int companyId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return;

        company.LastCheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private readonly record struct CompanyRef(int Id, string Name, string Url);
}

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


    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        var companies = await LoadUseCompaniesAsync(ct);
        var criteria = await LoadCriteriaAsync(ct);

        var scanned = 0;
        var failed = 0;
        var blocked = 0;
        var refused = 0;
        var refusedCompanies = new List<string>();
        var aiFailed = 0;
        var aiFailedCompanies = new List<string>();
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

            if (extracted is null)
            {
                // The page was read fine - the AI call is what failed. Counting this as
                // "0 adverts found" is how a whole broken run used to look like good news.
                logger.LogWarning(
                    "Read {Company}'s careers page but the AI could not extract any adverts from it. " +
                    "The page was fetched successfully, so this is an AI problem, not a site problem.",
                    company.Name);

                aiFailed++;
                aiFailedCompanies.Add(company.Name);
                continue;
            }

            var outcome = await upsert.UpsertForCompanyAsync(
                company.Id, "CareerPage", extracted, criteria, ct);

            totals += outcome;
            scanned++;

            await MarkCheckedAsync(company.Id, ct);

            logger.LogInformation(
                "{Company}: {Extracted} advert(s) found, {Inserted} new, {Updated} updated, {Rejected} filtered out",
                company.Name, extracted.Count, outcome.Inserted, outcome.Updated,
                outcome.RejectedTotal);
        }

        // Score everything pending, board-sourced listings included.
        var scored = await scoring.ScorePendingAsync(ct: ct);

        return new JobRunResult(
            Summarise(companies.Count, scanned, failed, blocked, refused, refusedCompanies, aiFailed, totals, scored),
            CollectIssues(companies.Count, failed, refused, refusedCompanies, aiFailed, aiFailedCompanies, scored));
    }

    /// <summary>The problems worth the human's attention, phrased so the cause is obvious
    /// without opening the logs first.</summary>
    private static List<string> CollectIssues(
        int companyCount, int failed, int refused, List<string> refusedCompanies,
        int aiFailed, List<string> aiFailedCompanies, ScoringOutcome scored)
    {
        var issues = new List<string>();

        if (aiFailed > 0)
        {
            var scope = aiFailed == companyCount
                ? $"all {companyCount} careers page(s)"
                : $"{aiFailed} of {companyCount} careers page(s)";

            issues.Add(
                $"The AI could not read adverts from {scope} ({Name(aiFailedCompanies)}). " +
                "The pages themselves downloaded fine, so no adverts were saved from them. " +
                "Check the AI provider settings and the log file for the underlying error.");
        }

        if (failed > 0)
            issues.Add($"{failed} careers page(s) could not be downloaded.");

        if (refused > 0)
            issues.Add($"{refused} site(s) refused automated access ({Name(refusedCompanies)}).");

        if (scored.NoCv)
            issues.Add("No CV has been uploaded, so nothing was scored. Add one on the Settings page.");

        if (scored.Failed > 0)
            issues.Add($"{scored.Failed} listing(s) could not be scored by the AI.");

        return issues;
    }

    private static string Name(List<string> names) =>
        names.Count <= 3
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    private async Task<JobScout.Core.Models.MatchCriteria> LoadCriteriaAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var config = await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppConfig();
        return ScoringService.ToCriteria(config);
    }

    private static string Summarise(
        int companyCount, int scanned, int failed, int blocked, int refused,
        List<string> refusedCompanies, int aiFailed, UpsertOutcome totals, ScoringOutcome scored)
    {
        var sb = new StringBuilder()
            .Append($"{scanned}/{companyCount} careers page(s) read");

        if (aiFailed > 0) sb.Append($", {aiFailed} read but not understood by the AI");
        if (failed > 0) sb.Append($", {failed} failed");
        if (blocked > 0) sb.Append($", {blocked} blocked by robots.txt");

        if (refused > 0)
        {
            sb.Append($", {refused} refused automated access ({string.Join(", ", refusedCompanies.Take(3))}")
              .Append(refusedCompanies.Count > 3 ? $" and {refusedCompanies.Count - 3} more)" : ")");
        }

        sb.Append($"; {totals.Inserted} new listing(s), {totals.Updated} updated");

        if (totals.RejectedTotal > 0)
            sb.Append($", {totals.RejectedTotal} filtered out ({RejectionSummary.Describe(totals)})");

        sb.Append($"; {scored.Scored} scored");

        if (scored.Skipped > 0) sb.Append($", {scored.Skipped} unchanged");
        if (scored.Failed > 0) sb.Append($", {scored.Failed} scoring failure(s)");
        if (scored.NoCv) sb.Append(" (no CV uploaded)");
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

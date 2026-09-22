using System.Text;
using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Web.Jobs;

/// <summary>Morning scan: read every USE company's adverts, filter by city and salary, upsert
/// by URL, then score everything new or unscored for USE companies - including board-sourced
/// listings, whose description gets fetched from the advert page when it is too thin to judge.
///
/// A company whose adverts live on a job-board service (Greenhouse, Lever, Ashby, Workable) is
/// read from that service's JSON feed: structured, complete, and no AI call to pull the adverts
/// out. Everyone else has their careers page read by the AI. Feeds are found by a short
/// detection pass before the scan, and whenever a careers page links to one.</summary>
public sealed class MorningScanJob(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IPageFetcher pageFetcher,
    IJobMatcher matcher,
    ListingUpsertService upsert,
    ScoringService scoring,
    AtsService ats,
    ILogger<MorningScanJob> logger) : IScheduledJob
{
    public const string Key = "morning-scan";


    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        var detected = await DetectFeedsAsync(ct);

        var companies = await LoadUseCompaniesAsync(ct);
        var criteria = await LoadCriteriaAsync(ct);

        var feedCompanies = 0;
        var feedsRead = 0;
        var feedFailedCompanies = new List<string>();
        var pagesAttempted = 0;
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

            if (ats.Enabled && company.HasFeed)
            {
                feedCompanies++;

                var fromFeed = await ats.FetchAsync(company.AtsKind, company.AtsToken!, ct);

                if (fromFeed is not null)
                {
                    var feedOutcome = await upsert.UpsertForCompanyAsync(
                        company.Id, company.AtsKind.ToString(), fromFeed, criteria, ct);

                    totals += feedOutcome;
                    feedsRead++;

                    await MarkCheckedAsync(company.Id, ct);

                    logger.LogInformation(
                        "{Company} ({Kind} feed): {Found} advert(s), {Inserted} new, {Updated} updated, {Rejected} filtered out",
                        company.Name, company.AtsKind, fromFeed.Count, feedOutcome.Inserted, feedOutcome.Updated,
                        feedOutcome.RejectedTotal);
                    continue;
                }

                feedFailedCompanies.Add(company.Name);

                if (string.IsNullOrWhiteSpace(company.Url))
                {
                    logger.LogWarning(
                        "Could not read the {Kind} feed for {Company}, and it has no careers page to fall back on",
                        company.AtsKind, company.Name);
                    continue;
                }

                logger.LogWarning("Could not read the {Kind} feed for {Company} - reading its careers page instead",
                    company.AtsKind, company.Name);
            }

            if (string.IsNullOrWhiteSpace(company.Url)) continue;

            pagesAttempted++;

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

            // A careers page that links to or embeds a job-board service gives the feed away.
            // Its adverts are then read from the feed, which also saves the AI call below.
            var source = "CareerPage";
            IReadOnlyList<ExtractedJob>? extracted = null;

            if (!company.HasFeed && await ats.DetectFromHtmlAsync(page.Html, ct) is { } onPage)
            {
                await ats.SaveDetectionAsync(company.Id, onPage, ct);
                detected.Add(Describe(company.Name, onPage));

                source = onPage.Kind.ToString();
                extracted = onPage.Jobs;
            }

            extracted ??= await matcher.ExtractJobsAsync(page.Text, company.Url, company.Name, ct);

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
                company.Id, source, extracted, criteria, ct);

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

        var summary = Summarise(pagesAttempted, scanned, failed, blocked, refused, refusedCompanies, aiFailed, totals, scored);

        if (feedCompanies > 0)
            summary = $"{feedsRead}/{feedCompanies} job-board feed(s) read, " + summary;

        if (detected.Count > 0)
            summary += $" New job-board feeds: {string.Join("; ", detected)}.";

        var issues = CollectIssues(pagesAttempted, failed, refused, refusedCompanies, aiFailed, aiFailedCompanies, scored);

        if (feedFailedCompanies.Count > 0)
        {
            issues.Insert(0,
                $"{feedFailedCompanies.Count} job-board feed(s) could not be read ({Name(feedFailedCompanies)}). " +
                "Any of them with a careers page was read from that instead. If this repeats, check the " +
                "service and token on the Companies page.");
        }

        return new JobRunResult(summary, issues);
    }

    /// <summary>Before the scan: look for a feed behind a few USE companies that have none -
    /// companies with no careers page first, since a feed is their only way in. Returns a line
    /// per feed found, for the run summary.</summary>
    private async Task<List<string>> DetectFeedsAsync(CancellationToken ct)
    {
        var found = new List<string>();

        if (!ats.Enabled || !ats.Config.DetectAutomatically || ats.Config.MaxDetectionsPerRun <= 0)
            return found;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, ats.Config.RecheckAfterDays));

        List<(int Id, string Name, string? Url)> due;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.Companies
                .AsNoTracking()
                .Where(c => c.Status == CompanyStatus.USE && c.AtsKind == AtsKind.None)
                .Where(c => c.AtsCheckedAt == null || c.AtsCheckedAt < cutoff)
                .OrderBy(c => c.Url != null && c.Url != "" ? 1 : 0)
                .ThenBy(c => c.AtsCheckedAt == null ? 0 : 1)
                .ThenBy(c => c.Name)
                .Take(ats.Config.MaxDetectionsPerRun)
                .Select(c => new { c.Id, c.Name, c.Url })
                .ToListAsync(ct);

            due = rows.Select(r => (r.Id, r.Name, r.Url)).ToList();
        }

        foreach (var company in due)
        {
            ct.ThrowIfCancellationRequested();

            var detection = await ats.DetectAsync(company.Name, company.Url, ct);
            await ats.SaveDetectionAsync(company.Id, detection, ct);

            if (detection is not null) found.Add(Describe(company.Name, detection));
        }

        return found;
    }

    private static string Describe(string company, AtsDetection d) =>
        $"{company} on {d.Kind} as '{d.Token}'" +
        (d.Guessed ? " (guessed from the name - check it is the right company on the Companies page)" : "");

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

    private async Task<MatchCriteria> LoadCriteriaAsync(CancellationToken ct)
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

        sb.Append('.');

        if (refused > 0)
        {
            sb.Append(" Sites that refuse automated access need the Playwright browsers installed, ")
              .Append("or are better reached through a job board on the Discovery run.");
        }

        return sb.ToString();
    }

    /// <summary>Every USE company with something to read: a careers page, a job-board feed,
    /// or both.</summary>
    private async Task<List<CompanyRef>> LoadUseCompaniesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Companies
            .AsNoTracking()
            .Where(c => c.Status == CompanyStatus.USE)
            .Where(c => (c.Url != null && c.Url != "") ||
                        (c.AtsKind != AtsKind.None && c.AtsToken != null && c.AtsToken != ""))
            .OrderBy(c => c.Name)
            .Select(c => new CompanyRef(c.Id, c.Name, c.Url, c.AtsKind, c.AtsToken))
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

    private readonly record struct CompanyRef(int Id, string Name, string? Url, AtsKind AtsKind, string? AtsToken)
    {
        public bool HasFeed => AtsKind != AtsKind.None && !string.IsNullOrWhiteSpace(AtsToken);
    }
}

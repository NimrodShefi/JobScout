using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Models;
using JobScout.Core.Options;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JobScout.Web.Jobs;

/// <summary>Evening discovery: for every active industry and every one of my cities, ask
/// each enabled job board. New companies land as REVIEW with a board link and no careers
/// URL; their listings are saved but deliberately NOT scored. Scoring only starts once I
/// move the company to USE.</summary>
public sealed class DiscoveryJob(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IEnumerable<IJobBoardProvider> boards,
    ListingUpsertService upsert,
    IOptions<JobScoutOptions> options,
    ILogger<DiscoveryJob> logger) : IScheduledJob
{
    public const string Key = "discovery";


    public async Task<JobRunResult> RunAsync(CancellationToken ct)
    {
        var enabled = boards.Where(b => b.IsEnabled).ToList();

        if (enabled.Count == 0)
            return "No job boards are enabled. Set JobScout:Boards:Adzuna:Enabled to true and add your keys.";

        var (industries, criteria) = await LoadInputsAsync(ct);

        if (industries.Count == 0)
            return "No active industries. Add one on the Industries page.";

        // No cities configured means one nationwide pass per industry.
        var cities = criteria.Cities.Count > 0 ? criteria.Cities : [null!];

        var totals = new UpsertOutcome();
        var newCompanies = 0;
        var queries = 0;
        var failedQueries = 0;
        var failedBoards = new HashSet<string>();

        // A review queue I cannot keep up with is worse than a short one, so a run adds this
        // many companies at most and then stops, leaving the rest for the next run.
        var cap = Math.Max(1, options.Value.Discovery.MaxNewCompaniesPerRun);
        var hitCap = false;

        foreach (var board in enabled)
        {
            foreach (var industry in industries)
            {
                foreach (var city in cities)
                {
                    ct.ThrowIfCancellationRequested();

                    var results = await board.SearchAsync(new BoardSearchRequest
                    {
                        Query = industry,
                        City = city,
                        MinimumSalary = criteria.MinimumSalary > 0 ? criteria.MinimumSalary : null,
                    }, ct);

                    queries++;

                    if (results is null)
                    {
                        // The board errored. Without this the run reports a clean zero and
                        // looks like the market was quiet.
                        failedQueries++;
                        failedBoards.Add(board.Name);
                        continue;
                    }

                    if (results.Count == 0) continue;

                    var (listings, created) = await upsert.UpsertBoardResultsAsync(
                        board.Name, results, criteria, cap - newCompanies, ct);

                    totals += listings;
                    newCompanies += created;

                    logger.LogInformation(
                        "{Board} '{Industry}' in {City}: {Results} result(s) -> {New} new listing(s), " +
                        "{Companies} new company(ies), {Filtered} filtered out",
                        board.Name, industry, city ?? "anywhere", results.Count,
                        listings.Inserted, created,
                        listings.RejectedTotal);

                    if (newCompanies >= cap)
                    {
                        hitCap = true;
                        logger.LogInformation(
                            "Stopping discovery early: the per-run cap of {Cap} new company(ies) " +
                            "has been reached", cap);
                        break;
                    }
                }

                if (hitCap) break;
            }

            if (hitCap) break;
        }

        var summary =
            $"{queries} board quer{(queries == 1 ? "y" : "ies")} across {enabled.Count} board(s); " +
            $"{newCompanies} new company(ies) awaiting review" +
            (hitCap ? " (per-run cap reached, scan stopped early)" : "") + ", " +
            $"{totals.Inserted} new listing(s), {totals.Updated} updated";

        if (failedQueries > 0) summary += $", {failedQueries} quer{(failedQueries == 1 ? "y" : "ies")} failed";

        if (totals.RejectedTotal > 0)
            summary += $", {totals.RejectedTotal} filtered out ({RejectionSummary.Describe(totals)})";

        summary += ". Listings are not scored until their company is set to USE.";

        var issues = new List<string>();

        if (failedQueries > 0)
        {
            issues.Add(
                $"{failedQueries} of {queries} board quer{(queries == 1 ? "y" : "ies")} failed " +
                $"({string.Join(", ", failedBoards)}), so this run saw less than the full picture.");
        }

        return new JobRunResult(summary, issues);
    }

    private async Task<(List<string> Industries, MatchCriteria Criteria)> LoadInputsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var industries = await db.Industries
            .AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.Name)
            .Select(i => i.Name)
            .ToListAsync(ct);

        var config = await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppConfig();

        return (industries, ScoringService.ToCriteria(config));
    }
}

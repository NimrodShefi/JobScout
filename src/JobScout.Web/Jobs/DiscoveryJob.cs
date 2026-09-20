using JobScout.Core.Abstractions;
using JobScout.Core.Entities;
using JobScout.Core.Models;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Web.Jobs;

/// <summary>Evening discovery: for every active industry and every one of my cities, ask
/// each enabled job board. New companies land as REVIEW with a board link and no careers
/// URL; their listings are saved but deliberately NOT scored. Scoring only starts once I
/// move the company to USE.</summary>
public sealed class DiscoveryJob(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    IEnumerable<IJobBoardProvider> boards,
    ListingUpsertService upsert,
    ILogger<DiscoveryJob> logger) : IScheduledJob
{
    public const string Key = "discovery";


    public async Task<string> RunAsync(CancellationToken ct)
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

                    if (results.Count == 0) continue;

                    var (listings, created) = await upsert.UpsertBoardResultsAsync(
                        board.Name, results, criteria, ct);

                    totals += listings;
                    newCompanies += created;

                    logger.LogInformation(
                        "{Board} '{Industry}' in {City}: {Results} result(s) -> {New} new listing(s), " +
                        "{Companies} new company(ies), {Filtered} filtered out",
                        board.Name, industry, city ?? "anywhere", results.Count,
                        listings.Inserted, created,
                        listings.RejectedTotal);
                }
            }
        }

        var summary =
            $"{queries} board quer{(queries == 1 ? "y" : "ies")} across {enabled.Count} board(s); " +
            $"{newCompanies} new company(ies) awaiting review, " +
            $"{totals.Inserted} new listing(s), {totals.Updated} updated";

        if (totals.RejectedTotal > 0)
            summary += $", {totals.RejectedTotal} filtered out ({RejectionSummary.Describe(totals)})";

        summary += ". Listings are not scored until their company is set to USE.";

        return summary;
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

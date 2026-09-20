using JobScout.Infrastructure.Services;

namespace JobScout.Web.Jobs;

/// <summary>Scores the unscored listings of one company. Queued when I move a company to
/// USE, so board-discovered adverts get scored without waiting for the next morning run.
///
/// Not part of the cron set - it is always triggered by an action of mine, so it does not
/// implement IScheduledJob.</summary>
public sealed class CompanyScoringJob(ScoringService scoring)
{
    public const string Key = "company-scoring";

    public async Task<string> RunForCompanyAsync(int companyId, CancellationToken ct)
    {
        var outcome = await scoring.ScorePendingAsync(companyId, ct);

        var summary = $"{outcome.Scored} scored, {outcome.Skipped} unchanged, {outcome.Failed} failed";
        if (outcome.DescriptionsFetched > 0) summary += $", {outcome.DescriptionsFetched} description(s) fetched";
        if (outcome.HitCap) summary += " (per-run cap reached)";

        return summary;
    }
}

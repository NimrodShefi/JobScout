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

    public async Task<JobRunResult> RunForCompanyAsync(int companyId, CancellationToken ct)
    {
        var outcome = await scoring.ScorePendingAsync(companyId, ct);

        var summary = $"{outcome.Scored} scored, {outcome.Skipped} unchanged, {outcome.Failed} failed";
        if (outcome.DescriptionsFetched > 0) summary += $", {outcome.DescriptionsFetched} description(s) fetched";
        if (outcome.NoCv) summary += " (no CV uploaded)";
        if (outcome.HitCap) summary += " (per-run cap reached)";

        var issues = new List<string>();

        if (outcome.NoCv)
            issues.Add("No CV has been uploaded, so nothing was scored. Add one on the Settings page.");

        if (outcome.Failed > 0)
            issues.Add($"{outcome.Failed} listing(s) could not be scored by the AI.");

        return new JobRunResult(summary, issues);
    }
}

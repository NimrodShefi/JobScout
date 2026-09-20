using System.Collections.Concurrent;
using Cronos;
using JobScout.Core.Options;
using JobScout.Web.Jobs;
using Microsoft.Extensions.Options;

namespace JobScout.Web.Services;

/// <summary>State of one job as the UI shows it.</summary>
public sealed record JobStatus(
    string Key,
    string DisplayName,
    string Description,
    string Cron,
    bool CronValid,
    DateTimeOffset? NextRunUtc,
    bool IsRunning);

/// <summary>Owns the cron schedules, runs the jobs, and stops them overlapping.
///
/// Overlap prevention is a per-job gate: a "Run now" click while the same job is already
/// running is rejected rather than queued, and the cron tick skips a job that is still
/// working from its previous fire.</summary>
public sealed class JobScheduler(
    IServiceScopeFactory scopeFactory,
    JobRunRecorder recorder,
    IOptions<JobScoutOptions> options,
    ILogger<JobScheduler> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, bool> _running = new();

    public SchedulingOptions Scheduling => options.Value.Scheduling;

    /// <summary>The cron-scheduled jobs and the expression each one runs on.</summary>
    public IReadOnlyList<ScheduledJobDefinition> Definitions =>
        JobCatalogue.All.Select(d => new ScheduledJobDefinition(d, CronFor(d.Key))).ToList();

    private string CronFor(string key) => key switch
    {
        MorningScanJob.Key => Scheduling.MorningScanCron,
        EmailCheckJob.Key => Scheduling.EmailCheckCron,
        DiscoveryJob.Key => Scheduling.DiscoveryCron,
        _ => string.Empty,
    };

    public TimeZoneInfo TimeZone => ResolveTimeZone(Scheduling.TimeZone);

    public bool IsRunning(string jobKey) => _running.TryGetValue(jobKey, out var running) && running;

    /// <summary>Status of every job for the Job runs page.</summary>
    public IReadOnlyList<JobStatus> GetStatuses()
    {
        var now = DateTimeOffset.UtcNow;
        var tz = TimeZone;

        // Deliberately does not construct the jobs: building one builds the AI client,
        // which throws when no API key is configured, and this page must still render.
        return Definitions.Select(d =>
        {
            var parsed = TryParseCron(d.Cron, out var expression);

            return new JobStatus(
                d.Key,
                d.Job.DisplayName,
                d.Job.Description,
                d.Cron,
                parsed,
                parsed ? expression!.GetNextOccurrence(now, tz) : null,
                IsRunning(d.Key));
        }).ToList();
    }

    /// <summary>Runs a job now unless it is already running. Returns false when it is.</summary>
    public async Task<bool> RunNowAsync(string jobKey, CancellationToken ct = default)
    {
        var definition = Definitions.FirstOrDefault(d => d.Key == jobKey);
        if (definition is null)
        {
            logger.LogWarning("Ignoring a request to run unknown job '{JobKey}'", jobKey);
            return false;
        }

        return await RunAsync(definition.Key, definition.Job.JobType, "manual", ct);
    }

    /// <summary>Scores one company's unscored listings. Used when a company moves to USE.
    /// Runs detached so the UI is not held up.</summary>
    public void QueueCompanyScoring(int companyId, string companyName)
    {
        var key = $"{CompanyScoringJob.Key}:{companyId}";

        _ = Task.Run(async () =>
        {
            var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            if (!await gate.WaitAsync(TimeSpan.Zero))
            {
                logger.LogInformation("Scoring for {Company} is already running - not starting another", companyName);
                return;
            }

            var runId = await recorder.StartAsync($"scoring: {companyName}");

            try
            {
                using var scope = scopeFactory.CreateScope();
                var job = scope.ServiceProvider.GetRequiredService<CompanyScoringJob>();

                var summary = await job.RunForCompanyAsync(companyId, CancellationToken.None);
                await recorder.FinishAsync(runId, success: true, summary, error: null);

                logger.LogInformation("Scoring for {Company} finished: {Summary}", companyName, summary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scoring for {Company} failed", companyName);
                await recorder.FinishAsync(runId, success: false, summary: null, error: ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>The shared run path: take the gate, open a run row, execute, close the row.
    /// Exceptions are recorded, never rethrown into the scheduler loop.</summary>
    internal async Task<bool> RunAsync(string jobKey, Type jobType, string trigger, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(jobKey, _ => new SemaphoreSlim(1, 1));

        // Zero timeout: if it is running, say so rather than piling up.
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            logger.LogInformation("'{JobKey}' is already running - {Trigger} trigger ignored", jobKey, trigger);
            return false;
        }

        _running[jobKey] = true;
        var runId = await recorder.StartAsync(jobKey, ct);

        logger.LogInformation("'{JobKey}' started ({Trigger}, run {RunId})", jobKey, trigger, runId);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var job = (IScheduledJob)scope.ServiceProvider.GetRequiredService(jobType);

            var summary = await job.RunAsync(ct);

            await recorder.FinishAsync(runId, success: true, summary, error: null);
            logger.LogInformation("'{JobKey}' finished (run {RunId}): {Summary}", jobKey, runId, summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await recorder.FinishAsync(runId, success: false, null, "Cancelled - the application was shutting down.");
            logger.LogWarning("'{JobKey}' was cancelled (run {RunId})", jobKey, runId);
        }
        catch (Exception ex)
        {
            // Full detail including the stack goes to the log files; the table keeps the message.
            logger.LogError(ex, "'{JobKey}' failed (run {RunId})", jobKey, runId);
            await recorder.FinishAsync(runId, success: false, null, ex.Message);
        }
        finally
        {
            _running[jobKey] = false;
            gate.Release();
        }

        return true;
    }

    internal static bool TryParseCron(string? cron, out CronExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(cron)) return false;

        // Six fields means the expression carries seconds, as the Quartz-style defaults do.
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        return CronExpression.TryParse(cron, format, out expression);
    }

    internal TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;

        try
        {
            // .NET resolves IANA ids such as "Europe/London" on Windows too.
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Unknown time zone '{TimeZone}' ({Error}) - using the machine's local zone",
                id, ex.Message);
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>A catalogue entry paired with the cron expression configuration gives it.</summary>
    public sealed record ScheduledJobDefinition(JobDefinition Job, string Cron)
    {
        public string Key => Job.Key;
    }
}

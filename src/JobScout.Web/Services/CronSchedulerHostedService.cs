using JobScout.Web.Services;

namespace JobScout.Web.Jobs;

/// <summary>Ticks once a minute, and fires any job whose next cron occurrence has passed.
///
/// A minute tick rather than a long precise delay means a laptop that sleeps through a
/// scheduled time wakes up, notices the occurrence is behind it, runs the job once, and
/// moves on - no burst of catch-up runs.</summary>
public sealed class CronSchedulerHostedService(
    JobScheduler scheduler,
    ILogger<CronSchedulerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>The occurrence each job is waiting for.</summary>
    private readonly Dictionary<string, DateTimeOffset> _nextDue = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!scheduler.Scheduling.Enabled)
        {
            logger.LogWarning("Scheduling is disabled in configuration - jobs will only run when triggered by hand");
            return;
        }

        var timeZone = scheduler.TimeZone;
        Prime(timeZone);

        logger.LogInformation("Cron scheduler started in {TimeZone}", timeZone.Id);

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            do
            {
                await TickAsync(timeZone, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Cron scheduler stopping");
        }
    }

    /// <summary>Works out the first occurrence of each job without firing anything.</summary>
    private void Prime(TimeZoneInfo timeZone)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var definition in scheduler.Definitions)
        {
            if (!JobScheduler.TryParseCron(definition.Cron, out var expression))
            {
                logger.LogError(
                    "Cron expression '{Cron}' for '{Key}' is not valid - that job will only run when triggered by hand",
                    definition.Cron, definition.Key);
                continue;
            }

            var next = expression!.GetNextOccurrence(now, timeZone);
            if (next is null)
            {
                logger.LogWarning("Cron '{Cron}' for '{Key}' has no future occurrence", definition.Cron, definition.Key);
                continue;
            }

            _nextDue[definition.Key] = next.Value;
            logger.LogInformation("'{Key}' scheduled with '{Cron}', next run {Next:u}",
                definition.Key, definition.Cron, next.Value);
        }
    }

    private async Task TickAsync(TimeZoneInfo timeZone, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var definition in scheduler.Definitions)
        {
            ct.ThrowIfCancellationRequested();

            if (!_nextDue.TryGetValue(definition.Key, out var due) || due > now)
                continue;

            // Advance first: a job that throws or overruns must not re-fire on the next tick.
            if (JobScheduler.TryParseCron(definition.Cron, out var expression))
            {
                var next = expression!.GetNextOccurrence(now, timeZone);
                if (next is not null) _nextDue[definition.Key] = next.Value;
                else _nextDue.Remove(definition.Key);
            }

            // Not awaited: a long scan must not hold up the other jobs' schedules.
            // The scheduler's own gate stops it overlapping with itself.
            _ = scheduler.RunAsync(definition.Key, definition.Job.JobType, "cron", ct);
        }

        await Task.CompletedTask;
    }
}

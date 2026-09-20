using JobScout.Core.Entities;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Web.Services;

/// <summary>Writes the one-row-per-run summary shown in the UI. Everything more detailed
/// belongs in the Serilog files, not here.</summary>
public sealed class JobRunRecorder(IDbContextFactory<JobScoutDbContext> dbFactory)
{
    /// <summary>Opens a run row. Each write is its own short transaction so a long run
    /// never holds the SQLite lock.</summary>
    public async Task<int> StartAsync(string jobName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var run = new JobRunLog
        {
            JobName = jobName,
            StartedAt = DateTimeOffset.UtcNow,
        };

        db.JobRunLogs.Add(run);
        await db.SaveChangesAsync(ct);

        return run.Id;
    }

    public async Task FinishAsync(int runId, bool success, string? summary, string? error, CancellationToken ct = default)
    {
        // Deliberately not the caller's token: a cancelled run still needs its row closed.
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);

        var run = await db.JobRunLogs.FirstOrDefaultAsync(r => r.Id == runId, CancellationToken.None);
        if (run is null) return;

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Success = success;
        run.Summary = Trim(summary, 2000);
        run.Error = Trim(error, 2000);

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];
}

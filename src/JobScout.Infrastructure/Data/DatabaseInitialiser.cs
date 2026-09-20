using JobScout.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobScout.Infrastructure.Data;

/// <summary>Applies migrations, turns on WAL, and makes sure the single AppConfig row exists.
/// Runs once at startup before the scheduler starts.</summary>
public sealed class DatabaseInitialiser(
    IDbContextFactory<JobScoutDbContext> factory,
    ILogger<DatabaseInitialiser> logger)
{
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        await db.Database.MigrateAsync(ct);

        // WAL lets the UI read while a background job writes. journal_mode is persistent,
        // busy_timeout is per-connection and is set in the connection string too.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (!await db.AppConfigs.AnyAsync(ct))
        {
            db.AppConfigs.Add(new AppConfig
            {
                Id = 1,
                Currency = "GBP",
                MinimumSalary = 0,
                IncludeRemote = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created default AppConfig row");
        }

        // Any run left marked as running by a crash is closed out now.
        var stranded = await db.JobRunLogs
            .Where(r => r.FinishedAt == null)
            .ToListAsync(ct);

        if (stranded.Count > 0)
        {
            foreach (var run in stranded)
            {
                run.FinishedAt = DateTimeOffset.UtcNow;
                run.Success = false;
                run.Error = "Interrupted - the application stopped while this run was in progress.";
            }
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Closed {Count} stranded job run(s) from a previous process", stranded.Count);
        }

        logger.LogInformation("Database ready");
    }
}

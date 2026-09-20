using JobScout.Core.Entities;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Infrastructure.Services;

/// <summary>The criteria exactly as the Settings form holds them: raw text, not yet parsed.</summary>
public sealed record CriteriaInput
{
    public decimal MinimumSalary { get; init; }
    public string Currency { get; init; } = "GBP";

    /// <summary>Comma-separated, as typed.</summary>
    public string? Cities { get; init; }

    public bool IncludeRemote { get; init; } = true;

    /// <summary>Comma-separated, as typed.</summary>
    public string? DesiredRoles { get; init; }

    /// <summary>Comma-separated, as typed.</summary>
    public string? ExcludedRoles { get; init; }
}

public sealed record CriteriaSaveResult(bool Saved, string? Error);

/// <summary>Reads and writes the single settings row.
///
/// This lives here rather than inside the Settings page so that "every field on the form is
/// actually persisted" is something a test can assert. An earlier version of the page
/// collected the two role lists and then quietly dropped them on save, which no test could
/// see while the logic lived in the component.</summary>
public sealed class AppConfigService(IDbContextFactory<JobScoutDbContext> dbFactory)
{
    public async Task<AppConfig> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AppConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppConfig();
    }

    /// <summary>Validates and saves the criteria. The CV is not touched here.</summary>
    public async Task<CriteriaSaveResult> SaveCriteriaAsync(CriteriaInput input, CancellationToken ct = default)
    {
        if (input.MinimumSalary < 0)
            return new CriteriaSaveResult(false, "Minimum salary cannot be negative.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var config = await db.AppConfigs.FirstOrDefaultAsync(ct);

        if (config is null)
        {
            config = new AppConfig { Id = 1 };
            db.AppConfigs.Add(config);
        }

        config.MinimumSalary = input.MinimumSalary;
        config.Currency = string.IsNullOrWhiteSpace(input.Currency) ? "GBP" : input.Currency.Trim();
        config.SetCities(AppConfig.ParseList(input.Cities));
        config.IncludeRemote = input.IncludeRemote;
        config.SetDesiredRoles(AppConfig.ParseList(input.DesiredRoles));
        config.SetExcludedRoles(AppConfig.ParseList(input.ExcludedRoles));
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return new CriteriaSaveResult(true, null);
    }
}

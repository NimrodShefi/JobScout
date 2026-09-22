using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Services;
using JobScout.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JobScout.Infrastructure.Services;

/// <summary>Creating and updating applications. Every status change writes a history row,
/// so the timeline in the UI is always complete.
///
/// This app never applies to a job on my behalf - "Applied" means I applied, and this
/// records that fact.</summary>
public sealed class ApplicationService(
    IDbContextFactory<JobScoutDbContext> dbFactory,
    ILogger<ApplicationService> logger)
{
    /// <summary>Records that I applied to a listing. Returns the existing application if
    /// one already points at that listing.</summary>
    public async Task<JobApplication> CreateFromListingAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Applications.FirstOrDefaultAsync(a => a.JobListingId == listingId, ct);
        if (existing is not null) return existing;

        var listing = await db.JobListings
            .Include(l => l.Company)
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);

        if (listing is null)
            throw new InvalidOperationException($"Listing {listingId} no longer exists.");

        var application = new JobApplication
        {
            JobListingId = listing.Id,
            CompanyName = listing.Company?.Name ?? "(unknown)",
            Title = listing.Title,
            AppliedAt = DateTimeOffset.UtcNow,
            Status = ApplicationStatus.Applied,
        };

        application.History.Add(new ApplicationStatusChange
        {
            FromStatus = null,
            ToStatus = ApplicationStatus.Applied,
            ChangedAt = DateTimeOffset.UtcNow,
            Source = StatusChangeSource.Manual,
            Note = "Marked as applied from the jobs list",
        });

        db.Applications.Add(application);

        listing.Status = JobListingStatus.Applied;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Recorded application {Id} for listing {ListingId}", application.Id, listing.Id);
        return application;
    }

    /// <summary>Records an application I made outside the app.</summary>
    public async Task<JobApplication> CreateManualAsync(
        string companyName,
        string title,
        DateTimeOffset appliedAt,
        string? notes,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var application = new JobApplication
        {
            CompanyName = companyName.Trim(),
            Title = title.Trim(),
            AppliedAt = appliedAt,
            Status = ApplicationStatus.Applied,
            Notes = notes,
        };

        application.History.Add(new ApplicationStatusChange
        {
            FromStatus = null,
            ToStatus = ApplicationStatus.Applied,
            ChangedAt = DateTimeOffset.UtcNow,
            Source = StatusChangeSource.Manual,
            Note = "Added by hand",
        });

        db.Applications.Add(application);
        await db.SaveChangesAsync(ct);

        return application;
    }

    /// <summary>Changes the status and records why. Manual changes are always allowed -
    /// the transition rules only constrain what an email may do on its own.</summary>
    public async Task ChangeStatusAsync(
        int applicationId,
        ApplicationStatus newStatus,
        StatusChangeSource source = StatusChangeSource.Manual,
        string? note = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (application is null) return;

        if (application.Status == newStatus) return;

        var from = application.Status;
        application.Status = newStatus;

        db.ApplicationStatusChanges.Add(new ApplicationStatusChange
        {
            JobApplicationId = application.Id,
            FromStatus = from,
            ToStatus = newStatus,
            ChangedAt = DateTimeOffset.UtcNow,
            Source = source,
            Note = note,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Application {Id} moved {From} -> {To} ({Source})",
            application.Id, from, newStatus, source);
    }

    /// <summary>Accepts a suggested update raised by the email job.</summary>
    public async Task AcceptSuggestionAsync(int suggestionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var suggestion = await db.SuggestedStatusUpdates
            .FirstOrDefaultAsync(s => s.Id == suggestionId && !s.IsResolved, ct);

        if (suggestion is null) return;

        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == suggestion.JobApplicationId, ct);
        if (application is null) return;

        suggestion.IsResolved = true;
        suggestion.ResolvedAt = DateTimeOffset.UtcNow;

        if (application.Status != suggestion.SuggestedStatus)
        {
            var from = application.Status;
            application.Status = suggestion.SuggestedStatus;

            db.ApplicationStatusChanges.Add(new ApplicationStatusChange
            {
                JobApplicationId = application.Id,
                FromStatus = from,
                ToStatus = suggestion.SuggestedStatus,
                ChangedAt = DateTimeOffset.UtcNow,
                Source = StatusChangeSource.EmailSuggestionAccepted,
                Note = suggestion.Rationale,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DismissSuggestionAsync(int suggestionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var suggestion = await db.SuggestedStatusUpdates
            .FirstOrDefaultAsync(s => s.Id == suggestionId && !s.IsResolved, ct);

        if (suggestion is null) return;

        suggestion.IsResolved = true;
        suggestion.ResolvedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves every application that has heard nothing for <paramref name="afterDays"/>
    /// days to NoResponse, with a history row. Returns how many moved.
    ///
    /// NoResponse is not terminal: a late reply can still move the application on, since the
    /// email check keeps matching against it.</summary>
    public async Task<int> MarkStaleAsync(int afterDays, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (afterDays <= 0) return 0;

        var at = now ?? DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.Applications
            .Where(a => a.Status == ApplicationStatus.Applied || a.Status == ApplicationStatus.Acknowledged)
            .Select(a => new
            {
                Application = a,
                // The creation row is stamped when I logged it, not when I applied, so it
                // would make a backdated application look fresh. AppliedAt covers it instead.
                LastChange = a.History
                    .Where(h => h.FromStatus != null)
                    .Max(h => (DateTimeOffset?)h.ChangedAt),
            })
            .ToListAsync(ct);

        var moved = 0;

        foreach (var c in candidates)
        {
            var a = c.Application;
            var lastActivity = Latest(a.AppliedAt, a.LastEmailAt, c.LastChange);

            if (!ApplicationStatusRules.IsStale(a.Status, lastActivity, at, afterDays)) continue;

            var from = a.Status;
            a.Status = ApplicationStatus.NoResponse;

            db.ApplicationStatusChanges.Add(new ApplicationStatusChange
            {
                JobApplicationId = a.Id,
                FromStatus = from,
                ToStatus = ApplicationStatus.NoResponse,
                ChangedAt = at,
                Source = StatusChangeSource.AgeRule,
                Note = $"Nothing heard for {afterDays} days",
            });

            moved++;
        }

        if (moved > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Marked {Count} application(s) as NoResponse after {Days} days of silence",
                moved, afterDays);
        }

        return moved;
    }

    private static DateTimeOffset Latest(DateTimeOffset first, params DateTimeOffset?[] others) =>
        others.Where(o => o is not null).Select(o => o!.Value).Append(first).Max();

    public async Task UpdateNotesAsync(int applicationId, string? notes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (application is null) return;

        application.Notes = notes;
        await db.SaveChangesAsync(ct);
    }
}

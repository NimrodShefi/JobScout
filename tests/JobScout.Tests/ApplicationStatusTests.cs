using JobScout.Core.Entities;
using JobScout.Core.Enums;
using JobScout.Core.Models;
using JobScout.Core.Services;
using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobScout.Tests;

/// <summary>The rules that decide what an email is allowed to do to an application on
/// its own, and that every change leaves a history row behind.</summary>
public class ApplicationStatusTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly ApplicationService _service;

    public ApplicationStatusTests() =>
        _service = new ApplicationService(_db, NullLogger<ApplicationService>.Instance);

    public void Dispose() => _db.Dispose();

    // ---- the pure rules -------------------------------------------------

    [Theory]
    [InlineData(EmailClassificationKind.Acknowledged, ApplicationStatus.Acknowledged)]
    [InlineData(EmailClassificationKind.Interview, ApplicationStatus.Interview)]
    [InlineData(EmailClassificationKind.Rejection, ApplicationStatus.Rejected)]
    [InlineData(EmailClassificationKind.Offer, ApplicationStatus.Offer)]
    public void Each_classification_maps_to_a_status(
        EmailClassificationKind kind, ApplicationStatus expected) =>
        Assert.Equal(expected, ApplicationStatusRules.FromClassification(kind));

    [Fact]
    public void An_unrelated_email_maps_to_no_status() =>
        Assert.Null(ApplicationStatusRules.FromClassification(EmailClassificationKind.Unrelated));

    [Theory]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Acknowledged)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Acknowledged, ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Interview, ApplicationStatus.Offer)]
    public void Forward_progress_is_allowed_automatically(
        ApplicationStatus from, ApplicationStatus to) =>
        Assert.True(ApplicationStatusRules.CanTransitionAutomatically(from, to));

    [Theory]
    [InlineData(ApplicationStatus.Interview, ApplicationStatus.Acknowledged)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Acknowledged, ApplicationStatus.Applied)]
    public void An_email_cannot_drag_an_application_backwards(
        ApplicationStatus from, ApplicationStatus to) =>
        Assert.False(ApplicationStatusRules.CanTransitionAutomatically(from, to));

    [Theory]
    [InlineData(ApplicationStatus.Applied)]
    [InlineData(ApplicationStatus.Acknowledged)]
    [InlineData(ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Offer)]
    public void A_rejection_may_always_land(ApplicationStatus from) =>
        Assert.True(ApplicationStatusRules.CanTransitionAutomatically(from, ApplicationStatus.Rejected));

    [Theory]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public void Nothing_moves_out_of_a_terminal_state_automatically(ApplicationStatus from)
    {
        Assert.True(ApplicationStatusRules.IsTerminal(from));
        Assert.False(ApplicationStatusRules.CanTransitionAutomatically(from, ApplicationStatus.Interview));
        Assert.False(ApplicationStatusRules.CanTransitionAutomatically(from, ApplicationStatus.Offer));
    }

    [Fact]
    public void A_transition_to_the_same_status_is_not_a_transition() =>
        Assert.False(ApplicationStatusRules.CanTransitionAutomatically(
            ApplicationStatus.Interview, ApplicationStatus.Interview));

    // ---- the service ----------------------------------------------------

    [Fact]
    public async Task Creating_an_application_records_the_first_history_row()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, notes: null);

        await using var db = _db.CreateDbContext();
        var history = await db.ApplicationStatusChanges
            .Where(h => h.JobApplicationId == application.Id)
            .ToListAsync();

        var row = Assert.Single(history);
        Assert.Null(row.FromStatus);
        Assert.Equal(ApplicationStatus.Applied, row.ToStatus);
        Assert.Equal(StatusChangeSource.Manual, row.Source);
    }

    [Fact]
    public async Task Every_status_change_adds_a_history_row()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, null);

        await _service.ChangeStatusAsync(application.Id, ApplicationStatus.Interview);
        await _service.ChangeStatusAsync(application.Id, ApplicationStatus.Offer);

        await using var db = _db.CreateDbContext();
        var history = await db.ApplicationStatusChanges
            .Where(h => h.JobApplicationId == application.Id)
            .OrderBy(h => h.Id)
            .ToListAsync();

        Assert.Equal(3, history.Count);
        Assert.Equal(ApplicationStatus.Applied, history[1].FromStatus);
        Assert.Equal(ApplicationStatus.Interview, history[1].ToStatus);
        Assert.Equal(ApplicationStatus.Offer, history[2].ToStatus);
    }

    [Fact]
    public async Task Setting_the_status_it_already_has_changes_nothing()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, null);

        await _service.ChangeStatusAsync(application.Id, ApplicationStatus.Applied);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.ApplicationStatusChanges
            .CountAsync(h => h.JobApplicationId == application.Id));
    }

    [Fact]
    public async Task I_can_move_an_application_backwards_by_hand_even_though_email_cannot()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, null);

        await _service.ChangeStatusAsync(application.Id, ApplicationStatus.Rejected);
        await _service.ChangeStatusAsync(application.Id, ApplicationStatus.Interview);

        await using var db = _db.CreateDbContext();
        var after = await db.Applications.FindAsync(application.Id);

        Assert.Equal(ApplicationStatus.Interview, after!.Status);
    }

    [Fact]
    public async Task Marking_a_listing_as_applied_creates_one_application_and_flags_the_listing()
    {
        var listingId = await SeedListingAsync();

        var application = await _service.CreateFromListingAsync(listingId);

        await using var db = _db.CreateDbContext();
        var listing = await db.JobListings.FindAsync(listingId);

        Assert.Equal(JobListingStatus.Applied, listing!.Status);
        Assert.Equal("Acme", application.CompanyName);
        Assert.Equal(listingId, application.JobListingId);
    }

    [Fact]
    public async Task Clicking_applied_twice_does_not_create_a_second_application()
    {
        var listingId = await SeedListingAsync();

        var first = await _service.CreateFromListingAsync(listingId);
        var second = await _service.CreateFromListingAsync(listingId);

        Assert.Equal(first.Id, second.Id);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Applications.CountAsync());
    }

    [Fact]
    public async Task Accepting_a_suggestion_applies_it_and_records_who_confirmed_it()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, null);

        int suggestionId;
        await using (var db = _db.CreateDbContext())
        {
            var suggestion = new SuggestedStatusUpdate
            {
                JobApplicationId = application.Id,
                SuggestedStatus = ApplicationStatus.Interview,
                Confidence = 0.55,
                Rationale = "Mentions a call next week",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.SuggestedStatusUpdates.Add(suggestion);
            await db.SaveChangesAsync();
            suggestionId = suggestion.Id;
        }

        await _service.AcceptSuggestionAsync(suggestionId);

        await using var check = _db.CreateDbContext();

        Assert.Equal(ApplicationStatus.Interview,
            (await check.Applications.FindAsync(application.Id))!.Status);

        Assert.True((await check.SuggestedStatusUpdates.FindAsync(suggestionId))!.IsResolved);

        var latest = await check.ApplicationStatusChanges
            .Where(h => h.JobApplicationId == application.Id)
            .OrderByDescending(h => h.Id)
            .FirstAsync();

        Assert.Equal(StatusChangeSource.EmailSuggestionAccepted, latest.Source);
    }

    [Fact]
    public async Task Dismissing_a_suggestion_resolves_it_and_leaves_the_status_alone()
    {
        var application = await _service.CreateManualAsync(
            "Acme", "Engineer", DateTimeOffset.UtcNow, null);

        int suggestionId;
        await using (var db = _db.CreateDbContext())
        {
            var suggestion = new SuggestedStatusUpdate
            {
                JobApplicationId = application.Id,
                SuggestedStatus = ApplicationStatus.Rejected,
                Confidence = 0.4,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.SuggestedStatusUpdates.Add(suggestion);
            await db.SaveChangesAsync();
            suggestionId = suggestion.Id;
        }

        await _service.DismissSuggestionAsync(suggestionId);

        await using var check = _db.CreateDbContext();

        Assert.True((await check.SuggestedStatusUpdates.FindAsync(suggestionId))!.IsResolved);
        Assert.Equal(ApplicationStatus.Applied,
            (await check.Applications.FindAsync(application.Id))!.Status);
    }

    private async Task<int> SeedListingAsync()
    {
        await using var db = _db.CreateDbContext();

        var company = new Company
        {
            Name = "Acme",
            NormalisedName = CompanyNameNormaliser.Normalise("Acme"),
            Status = CompanyStatus.USE,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var listing = new JobListing
        {
            CompanyId = company.Id,
            Title = "Engineer",
            Url = "https://acme.test/jobs/1",
            Source = "CareerPage",
            FoundAt = DateTimeOffset.UtcNow,
        };
        db.JobListings.Add(listing);
        await db.SaveChangesAsync();

        return listing.Id;
    }
}

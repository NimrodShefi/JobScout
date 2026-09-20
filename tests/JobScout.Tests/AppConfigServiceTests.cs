using JobScout.Infrastructure.Services;
using JobScout.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Tests;

/// <summary>The Settings save path.
///
/// These exist because an earlier version of the Settings page collected the two role lists
/// and then silently dropped them on save. Nothing could catch that while the logic lived in
/// the component, so it lives here now and every field is asserted.</summary>
public class AppConfigServiceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly AppConfigService _service;

    public AppConfigServiceTests() => _service = new AppConfigService(_db);

    public void Dispose() => _db.Dispose();

    private static CriteriaInput FullyPopulated => new()
    {
        MinimumSalary = 75_000,
        Currency = "GBP",
        Cities = "London, Manchester",
        IncludeRemote = false,
        DesiredRoles = ".NET developer, backend engineer",
        ExcludedRoles = "senior, principal",
    };

    [Fact]
    public async Task Every_field_on_the_form_is_actually_persisted()
    {
        var result = await _service.SaveCriteriaAsync(FullyPopulated);

        Assert.True(result.Saved);

        await using var db = _db.CreateDbContext();
        var config = await db.AppConfigs.SingleAsync();

        Assert.Equal(75_000, config.MinimumSalary);
        Assert.Equal("GBP", config.Currency);
        Assert.Equal(["London", "Manchester"], config.CityList);
        Assert.False(config.IncludeRemote);
        Assert.Equal([".NET developer", "backend engineer"], config.DesiredRoleList);
        Assert.Equal(["senior", "principal"], config.ExcludedRoleList);
        Assert.NotEqual(default, config.UpdatedAt);
    }

    [Fact]
    public async Task The_saved_criteria_are_what_scoring_then_uses()
    {
        // The whole point of the settings: they must reach the filter and the AI.
        await _service.SaveCriteriaAsync(FullyPopulated);

        var criteria = ScoringService.ToCriteria(await _service.GetAsync());

        Assert.Equal(75_000, criteria.MinimumSalary);
        Assert.Equal(["London", "Manchester"], criteria.Cities);
        Assert.False(criteria.IncludeRemote);
        Assert.Equal([".NET developer", "backend engineer"], criteria.DesiredRoles);
        Assert.Equal(["senior", "principal"], criteria.ExcludedRoles);
    }

    [Fact]
    public async Task Saving_again_replaces_the_previous_values_rather_than_appending()
    {
        await _service.SaveCriteriaAsync(FullyPopulated);

        await _service.SaveCriteriaAsync(FullyPopulated with
        {
            DesiredRoles = "data engineer",
            ExcludedRoles = "",
        });

        var config = await _service.GetAsync();

        Assert.Equal(["data engineer"], config.DesiredRoleList);
        Assert.Empty(config.ExcludedRoleList);
    }

    [Fact]
    public async Task Clearing_a_list_really_clears_it()
    {
        await _service.SaveCriteriaAsync(FullyPopulated);
        await _service.SaveCriteriaAsync(FullyPopulated with { Cities = null, DesiredRoles = null });

        var config = await _service.GetAsync();

        Assert.Empty(config.CityList);
        Assert.Empty(config.DesiredRoleList);
        Assert.Equal(string.Empty, config.DesiredRoles);
    }

    [Fact]
    public async Task Whatever_spacing_is_typed_is_stored_tidily()
    {
        await _service.SaveCriteriaAsync(FullyPopulated with
        {
            DesiredRoles = "  .NET developer ,,  backend engineer , ",
        });

        var config = await _service.GetAsync();

        Assert.Equal(".NET developer, backend engineer", config.DesiredRoles);
    }

    [Fact]
    public async Task A_negative_minimum_salary_is_refused_and_nothing_is_written()
    {
        await _service.SaveCriteriaAsync(FullyPopulated);

        var result = await _service.SaveCriteriaAsync(FullyPopulated with { MinimumSalary = -1 });

        Assert.False(result.Saved);
        Assert.Contains("negative", result.Error);

        // The earlier save is intact.
        Assert.Equal(75_000, (await _service.GetAsync()).MinimumSalary);
    }

    [Fact]
    public async Task A_blank_currency_falls_back_to_the_default()
    {
        await _service.SaveCriteriaAsync(FullyPopulated with { Currency = "  " });

        Assert.Equal("GBP", (await _service.GetAsync()).Currency);
    }

    [Fact]
    public async Task The_cv_is_left_alone_by_a_criteria_save()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.AppConfigs.Add(new JobScout.Core.Entities.AppConfig
            {
                Id = 1,
                CvFileName = "cv.pdf",
                CvText = "Ten years of C#.",
            });
            await db.SaveChangesAsync();
        }

        await _service.SaveCriteriaAsync(FullyPopulated);

        var after = await _service.GetAsync();

        Assert.Equal("cv.pdf", after.CvFileName);
        Assert.Equal("Ten years of C#.", after.CvText);
    }
}

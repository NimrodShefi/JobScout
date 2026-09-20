using JobScout.Core.Models;

namespace JobScout.Core.Services;

/// <summary>Why a listing was dropped, for the run summary.</summary>
public enum FilterOutcome
{
    Keep = 0,
    RejectedLocation = 1,
    RejectedSalary = 2,
}

/// <summary>Salary and location rules. Pure and side-effect free so it can be unit tested
/// without a database or a network.</summary>
public static class ListingFilter
{
    /// <summary>A listing is kept when its location matches one of my cities (or it is remote
    /// and remote is allowed) AND its salary is either unknown or at/above my minimum.</summary>
    public static FilterOutcome Evaluate(
        string? location,
        bool isRemote,
        decimal? salaryMin,
        decimal? salaryMax,
        MatchCriteria criteria)
    {
        if (!LocationMatches(location, isRemote, criteria))
            return FilterOutcome.RejectedLocation;

        if (!SalaryMatches(salaryMin, salaryMax, criteria.MinimumSalary))
            return FilterOutcome.RejectedSalary;

        return FilterOutcome.Keep;
    }

    public static bool LocationMatches(string? location, bool isRemote, MatchCriteria criteria)
    {
        if (isRemote || LooksRemote(location))
            return criteria.IncludeRemote;

        // No cities configured means "anywhere".
        if (criteria.Cities.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(location))
            return true; // Unknown location is kept; the AI or I can judge it later.

        return criteria.Cities.Any(city =>
            !string.IsNullOrWhiteSpace(city) &&
            location.Contains(city.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool LooksRemote(string? location) =>
        !string.IsNullOrWhiteSpace(location) &&
        (location.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
         location.Contains("work from home", StringComparison.OrdinalIgnoreCase) ||
         location.Contains("anywhere", StringComparison.OrdinalIgnoreCase));

    /// <summary>Unknown salary is kept deliberately - those listings are flagged, not dropped.
    /// A listing passes if the top of its range reaches the minimum.</summary>
    public static bool SalaryMatches(decimal? salaryMin, decimal? salaryMax, decimal minimumSalary)
    {
        if (minimumSalary <= 0) return true;

        var best = salaryMax ?? salaryMin;
        if (best is null) return true; // Unknown - keep and flag.

        return best.Value >= minimumSalary;
    }

    public static bool IsSalaryKnown(decimal? salaryMin, decimal? salaryMax) =>
        salaryMin.HasValue || salaryMax.HasValue;
}

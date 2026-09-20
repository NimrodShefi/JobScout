using JobScout.Core.Models;

namespace JobScout.Core.Services;

/// <summary>Why a listing was dropped, for the run summary.</summary>
public enum FilterOutcome
{
    Keep = 0,
    RejectedLocation = 1,
    RejectedSalary = 2,

    /// <summary>The title matched one of my excluded words, e.g. "senior".</summary>
    RejectedExcludedRole = 3,

    /// <summary>I listed the roles I want and this title is not one of them.</summary>
    RejectedUnwantedRole = 4,
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
        MatchCriteria criteria) =>
        Evaluate(title: null, location, isRemote, salaryMin, salaryMax, criteria);

    /// <summary>The full rule set. Title is checked first because it is the cheapest test
    /// and the one most likely to rule a listing out.</summary>
    public static FilterOutcome Evaluate(
        string? title,
        string? location,
        bool isRemote,
        decimal? salaryMin,
        decimal? salaryMax,
        MatchCriteria criteria)
    {
        var titleOutcome = EvaluateTitle(title, criteria);
        if (titleOutcome != FilterOutcome.Keep) return titleOutcome;

        if (!LocationMatches(location, isRemote, criteria))
            return FilterOutcome.RejectedLocation;

        if (!SalaryMatches(salaryMin, salaryMax, criteria.MinimumSalary))
            return FilterOutcome.RejectedSalary;

        return FilterOutcome.Keep;
    }

    /// <summary>Exclusions win over wanted roles: "Senior .NET Developer" is still excluded
    /// by "senior" even when ".NET developer" is on the wanted list.</summary>
    public static FilterOutcome EvaluateTitle(string? title, MatchCriteria criteria)
    {
        if (RoleMatcher.MatchesAny(title, criteria.ExcludedRoles))
            return FilterOutcome.RejectedExcludedRole;

        // No wanted roles configured means no constraint, not "want nothing".
        if (criteria.DesiredRoles.Count == 0)
            return FilterOutcome.Keep;

        // A title we could not read cannot be judged, so it is kept for the AI to weigh.
        if (string.IsNullOrWhiteSpace(title))
            return FilterOutcome.Keep;

        return RoleMatcher.MatchesAny(title, criteria.DesiredRoles)
            ? FilterOutcome.Keep
            : FilterOutcome.RejectedUnwantedRole;
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

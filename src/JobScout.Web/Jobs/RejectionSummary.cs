using JobScout.Infrastructure.Services;

namespace JobScout.Web.Jobs;

/// <summary>Turns the filter counters into the phrase the run summary shows.
/// Only reasons that actually fired are listed, so the line stays readable.</summary>
internal static class RejectionSummary
{
    public static string Describe(UpsertOutcome totals)
    {
        var parts = new List<string>(4);

        if (totals.RejectedUnwantedRole > 0)
            parts.Add($"{totals.RejectedUnwantedRole} not a role you want");

        if (totals.RejectedExcludedRole > 0)
            parts.Add($"{totals.RejectedExcludedRole} excluded by role");

        if (totals.RejectedLocation > 0)
            parts.Add($"{totals.RejectedLocation} location");

        if (totals.RejectedSalary > 0)
            parts.Add($"{totals.RejectedSalary} salary");

        return string.Join(", ", parts);
    }
}

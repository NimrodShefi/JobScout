using JobScout.Core.Enums;
using JobScout.Core.Models;

namespace JobScout.Core.Services;

/// <summary>Which application status transitions are allowed, and how an email
/// classification maps onto a status.</summary>
public static class ApplicationStatusRules
{
    /// <summary>Terminal states. Nothing moves out of these automatically.</summary>
    public static bool IsTerminal(ApplicationStatus status) =>
        status is ApplicationStatus.Rejected or ApplicationStatus.Withdrawn;

    public static ApplicationStatus? FromClassification(EmailClassificationKind kind) => kind switch
    {
        EmailClassificationKind.Acknowledged => ApplicationStatus.Acknowledged,
        EmailClassificationKind.Interview => ApplicationStatus.Interview,
        EmailClassificationKind.Rejection => ApplicationStatus.Rejected,
        EmailClassificationKind.Offer => ApplicationStatus.Offer,
        _ => null,
    };

    /// <summary>Rank used to stop an email dragging an application backwards, e.g. a late
    /// "we received your application" after an interview was already booked.</summary>
    public static int Rank(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Applied => 0,
        ApplicationStatus.Acknowledged => 1,
        ApplicationStatus.Interview => 2,
        ApplicationStatus.Offer => 3,
        _ => -1,
    };

    /// <summary>An automatic transition is allowed when the current state is not terminal,
    /// the target differs, and it is either a terminal outcome or genuine forward progress.</summary>
    public static bool CanTransitionAutomatically(ApplicationStatus from, ApplicationStatus to)
    {
        if (IsTerminal(from)) return false;
        if (from == to) return false;

        // Rejection and withdrawal may always land.
        if (IsTerminal(to)) return true;

        return Rank(to) > Rank(from);
    }
}

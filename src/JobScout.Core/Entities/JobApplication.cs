using JobScout.Core.Enums;

namespace JobScout.Core.Entities;

/// <summary>An application I have made. Named JobApplication to avoid clashing with
/// framework types called Application.</summary>
public class JobApplication
{
    public int Id { get; set; }

    /// <summary>Null when I logged the application by hand with no listing behind it.</summary>
    public int? JobListingId { get; set; }
    public JobListing? JobListing { get; set; }

    /// <summary>Denormalised so the record survives the listing being removed.</summary>
    public string CompanyName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset AppliedAt { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Applied;

    /// <summary>When an email relating to this application last arrived.</summary>
    public DateTimeOffset? LastEmailAt { get; set; }

    public string? Notes { get; set; }

    public List<ApplicationStatusChange> History { get; set; } = [];
    public List<SuggestedStatusUpdate> Suggestions { get; set; } = [];

    public bool IsOpen => Status is ApplicationStatus.Applied
        or ApplicationStatus.Acknowledged
        or ApplicationStatus.Interview
        or ApplicationStatus.Offer;
}

/// <summary>One row per status transition, for the history view.</summary>
public class ApplicationStatusChange
{
    public int Id { get; set; }

    public int JobApplicationId { get; set; }
    public JobApplication? JobApplication { get; set; }

    public ApplicationStatus? FromStatus { get; set; }
    public ApplicationStatus ToStatus { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public StatusChangeSource Source { get; set; } = StatusChangeSource.Manual;

    /// <summary>Short human-readable reason. Never an email body.</summary>
    public string? Note { get; set; }
}

/// <summary>A low-confidence email match the AI produced. Shown for me to confirm;
/// never applied automatically.</summary>
public class SuggestedStatusUpdate
{
    public int Id { get; set; }

    public int JobApplicationId { get; set; }
    public JobApplication? JobApplication { get; set; }

    public ApplicationStatus SuggestedStatus { get; set; }

    /// <summary>0-1 confidence reported by the matcher.</summary>
    public double Confidence { get; set; }

    /// <summary>One-line rationale. Deliberately short - no email body is stored.</summary>
    public string? Rationale { get; set; }

    /// <summary>Sender address and subject only, for me to recognise the message.</summary>
    public string? EmailFrom { get; set; }
    public string? EmailSubject { get; set; }
    public DateTimeOffset? EmailReceivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsResolved { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

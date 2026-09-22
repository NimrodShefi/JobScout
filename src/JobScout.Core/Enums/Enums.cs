namespace JobScout.Core.Enums;

/// <summary>Whether a company should be scanned for jobs.</summary>
public enum CompanyStatus
{
    /// <summary>Scan this company's careers page on every morning run.</summary>
    USE = 0,
    /// <summary>Never scan. Kept so it is not rediscovered endlessly.</summary>
    NOT_USE = 1,
    /// <summary>Discovered from a job board, waiting for me to triage it.</summary>
    REVIEW = 2,
}

/// <summary>The hosted job-board service a company publishes its adverts through. Each one
/// has a public JSON feed, which is read instead of scraping the careers page.</summary>
public enum AtsKind
{
    None = 0,
    Greenhouse = 1,
    Lever = 2,
    Ashby = 3,
    Workable = 4,
}

public enum JobListingStatus
{
    New = 0,
    Scored = 1,
    Dismissed = 2,
    Applied = 3,
}

public enum ApplicationStatus
{
    Applied = 0,
    Acknowledged = 1,
    Interview = 2,
    Offer = 3,
    Rejected = 4,
    Withdrawn = 5,
    NoResponse = 6,
}

/// <summary>How the AI classified an inbound email relative to an application.</summary>
public enum EmailClassification
{
    Unrelated = 0,
    Acknowledged = 1,
    Interview = 2,
    Rejection = 3,
    Offer = 4,
}

/// <summary>Who or what caused an application status change.</summary>
public enum StatusChangeSource
{
    Manual = 0,
    EmailAutomatic = 1,
    EmailSuggestionAccepted = 2,
    /// <summary>The no-response rule: nothing heard for the configured number of days.</summary>
    AgeRule = 3,
}

/// <summary>How a job run ended, as the dashboard reports it. A run that finished but hit
/// problems is deliberately not "ok" - silently reporting zero results as success is what
/// let a total AI outage look like a quiet morning.</summary>
public enum JobRunOutcome
{
    Running = 0,
    Ok = 1,
    /// <summary>Finished and saved its work, but something needs a look in the logs.</summary>
    CompletedWithIssues = 2,
    /// <summary>Threw - nothing useful came back.</summary>
    Failed = 3,
}

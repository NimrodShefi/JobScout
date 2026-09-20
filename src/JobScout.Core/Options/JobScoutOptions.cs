namespace JobScout.Core.Options;

/// <summary>Root configuration section "JobScout". Secrets come from user secrets
/// or environment variables and are never persisted to the database.</summary>
public sealed class JobScoutOptions
{
    public const string SectionName = "JobScout";

    public AiOptions Ai { get; set; } = new();
    public FetchOptions Fetching { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
    public BoardOptions Boards { get; set; } = new();
    public SchedulingOptions Scheduling { get; set; } = new();
    public ScoringOptions Scoring { get; set; } = new();
}

public enum AiProvider
{
    Anthropic = 0,
    OpenAI = 1,
    AzureOpenAI = 2,
    Ollama = 3,
}

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Anthropic;

    /// <summary>Model id. Defaults are applied per provider when this is blank.</summary>
    public string? Model { get; set; }

    /// <summary>Put this in user secrets, not appsettings.json.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Required for Azure OpenAI; optional override for OpenAI; base URL for Ollama.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure deployment name when it differs from the model id.</summary>
    public string? DeploymentName { get; set; }

    /// <summary>How many times to re-ask when the model returns unusable JSON.</summary>
    public int MaxJsonRetries { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Job descriptions are truncated to this before being sent, to cap cost.</summary>
    public int MaxDescriptionChars { get; set; } = 12_000;

    /// <summary>CV text is truncated to this before being sent.</summary>
    public int MaxCvChars { get; set; } = 20_000;
}

public sealed class FetchOptions
{
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Simultaneous outbound page fetches across the whole app.</summary>
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>Pause after each fetch to the same host.</summary>
    public int PolitenessDelayMs { get; set; } = 1500;

    public string UserAgent { get; set; } =
        "JobScout/1.0 (personal job search tool; single user; contact: local)";

    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>Turn off to run without Playwright browsers installed.</summary>
    public bool EnableBrowserFallback { get; set; } = true;

    /// <summary>Retry with the headless browser when a site answers our plain HTTP client
    /// with 401, 403 or 429.
    ///
    /// Large careers sites sit behind bot protection that rejects any client which does not
    /// look like a browser, often including robots.txt itself. Retrying through real Chromium
    /// is how a normal visitor would see the page. robots.txt still decides what may be
    /// fetched at all - this only changes which client does the fetching, never whether a
    /// disallowed path is allowed.</summary>
    public bool RetryBlockedPagesWithBrowser { get; set; } = true;

    /// <summary>Upper bound on a host's declared Crawl-delay, so one unusually large value
    /// cannot stall an entire run.</summary>
    public int MaxCrawlDelaySeconds { get; set; } = 30;

    /// <summary>Below this many characters of visible text the page is treated as JS-rendered.</summary>
    public int JsHeuristicMinTextLength { get; set; } = 600;

    public int MaxPageTextChars { get; set; } = 60_000;
}

public enum EmailProviderKind
{
    None = 0,
    Imap = 1,
}

public sealed class EmailOptions
{
    public EmailProviderKind Provider { get; set; } = EmailProviderKind.None;

    public ImapOptions Imap { get; set; } = new();

    /// <summary>How much of a message body is sent to the AI. Bodies are never stored.</summary>
    public int BodySnippetChars { get; set; } = 1_500;

    /// <summary>At or above this confidence the status is changed automatically;
    /// below it the result is recorded as a suggestion for me to confirm.</summary>
    public double AutoApplyConfidence { get; set; } = 0.8;

    public int MaxMessagesPerRun { get; set; } = 100;

    /// <summary>First run only: how far back to look.</summary>
    public int InitialLookbackDays { get; set; } = 14;
}

public sealed class ImapOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }

    /// <summary>User secrets only. For Gmail this is an app password.</summary>
    public string? Password { get; set; }

    public string Folder { get; set; } = "INBOX";
}

public sealed class BoardOptions
{
    public AdzunaOptions Adzuna { get; set; } = new();
}

public sealed class AdzunaOptions
{
    public bool Enabled { get; set; }
    public string? AppId { get; set; }
    public string? AppKey { get; set; }

    /// <summary>Adzuna country code, e.g. "gb", "us".</summary>
    public string Country { get; set; } = "gb";

    public int MaxResultsPerQuery { get; set; } = 50;
}

public sealed class SchedulingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>IANA id, e.g. "Europe/London".</summary>
    public string TimeZone { get; set; } = "Europe/London";

    public string MorningScanCron { get; set; } = "0 0 7 * * ?";
    public string EmailCheckCron { get; set; } = "0 0 19 * * ?";
    public string DiscoveryCron { get; set; } = "0 30 19 * * ?";
}

public sealed class ScoringOptions
{
    /// <summary>Hard cap on AI scoring calls per run, to control cost.</summary>
    public int MaxListingsPerRun { get; set; } = 50;

    /// <summary>Simultaneous scoring calls.</summary>
    public int MaxConcurrency { get; set; } = 2;
}

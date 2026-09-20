using JobScout.Core.Abstractions;
using JobScout.Core.Models;

namespace JobScout.Tests.TestSupport;

/// <summary>A matcher that returns whatever the test tells it to, and records what it was
/// asked. No network, no API key, no cost.</summary>
public sealed class FakeJobMatcher : IJobMatcher
{
    public Func<string, string?, string, JobMatchResult?> ScoreHandler { get; set; } =
        (_, _, _) => new JobMatchResult { Score = 70, Reasoning = "fake" };

    public Func<string, string, string, IReadOnlyList<ExtractedJob>> ExtractHandler { get; set; } =
        (_, _, _) => [];

    public Func<IReadOnlyList<(int Id, string Company, string Title)>, string, string?, string?, EmailMatchResult?>
        ClassifyHandler
    { get; set; } = (_, _, _, _) => new EmailMatchResult { Classification = EmailClassificationKind.Unrelated };

    public List<string> ScoredTitles { get; } = [];
    public int ScoreCalls => ScoredTitles.Count;
    public int ClassifyCalls { get; private set; }

    public Task<JobMatchResult?> ScoreAsync(
        string cvText, MatchCriteria criteria, string jobTitle, string? jobLocation,
        string jobDescription, CancellationToken ct = default)
    {
        ScoredTitles.Add(jobTitle);
        return Task.FromResult(ScoreHandler(jobTitle, jobLocation, jobDescription));
    }

    public Task<IReadOnlyList<ExtractedJob>> ExtractJobsAsync(
        string pageText, string pageUrl, string companyName, CancellationToken ct = default) =>
        Task.FromResult(ExtractHandler(pageText, pageUrl, companyName));

    public Task<EmailMatchResult?> ClassifyEmailAsync(
        IReadOnlyList<(int Id, string Company, string Title)> openApplications,
        string fromAddress, string? subject, string? bodySnippet, CancellationToken ct = default)
    {
        ClassifyCalls++;
        return Task.FromResult(ClassifyHandler(openApplications, fromAddress, subject, bodySnippet));
    }
}

/// <summary>Serves canned pages by URL. Anything not registered comes back as a failure,
/// which is what a real fetch of a dead careers page looks like.</summary>
public sealed class FakePageFetcher : IPageFetcher
{
    public Dictionary<string, string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RobotsBlocked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requested { get; } = [];

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        Requested.Add(url);

        if (RobotsBlocked.Contains(url))
        {
            return Task.FromResult(new PageFetchResult
            {
                Url = url,
                Success = false,
                BlockedByRobots = true,
                Error = "Disallowed by robots.txt.",
            });
        }

        if (!Pages.TryGetValue(url, out var text))
            return Task.FromResult(PageFetchResult.Failed(url, "404"));

        return Task.FromResult(new PageFetchResult
        {
            Url = url,
            Success = true,
            Html = $"<html><body>{text}</body></html>",
            Text = text,
            StatusCode = 200,
        });
    }
}

/// <summary>Hands back a scripted batch of messages and records the cursor it was given.</summary>
public sealed class FakeEmailProvider : IEmailProvider
{
    public string MailboxKey => "fake:inbox";
    public bool IsConfigured { get; set; } = true;

    public List<EmailMessage> Messages { get; } = [];
    public EmailFetchCursor? LastCursor { get; private set; }
    public uint? UidValidity { get; set; } = 1;

    public Task<EmailFetchResult> FetchSinceAsync(EmailFetchCursor cursor, CancellationToken ct = default)
    {
        LastCursor = cursor;

        return Task.FromResult(new EmailFetchResult
        {
            Messages = Messages,
            UidValidity = UidValidity,
            HighestUid = Messages.Count == 0 ? null : Messages.Max(m => m.Uid),
        });
    }
}

/// <summary>A board that returns whatever the test put in it.</summary>
public sealed class FakeJobBoardProvider(string name = "FakeBoard") : IJobBoardProvider
{
    public string Name { get; } = name;
    public bool IsEnabled { get; set; } = true;

    public List<BoardJobResult> Results { get; } = [];
    public List<BoardSearchRequest> Requests { get; } = [];

    public Task<IReadOnlyList<BoardJobResult>> SearchAsync(
        BoardSearchRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult<IReadOnlyList<BoardJobResult>>(Results);
    }
}

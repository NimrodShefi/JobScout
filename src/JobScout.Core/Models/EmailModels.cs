namespace JobScout.Core.Models;

/// <summary>An inbound message, already trimmed. The body snippet is passed to the AI
/// and then dropped - it is never persisted.</summary>
public sealed record EmailMessage
{
    public required string MessageId { get; init; }
    public uint Uid { get; init; }

    public string? FromName { get; init; }
    public required string FromAddress { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Truncated plain-text body. Length is capped by configuration.</summary>
    public string? BodySnippet { get; init; }
}

/// <summary>Where the reader should resume from.</summary>
public sealed record EmailFetchCursor
{
    public DateTimeOffset? Since { get; init; }
    public uint? UidValidity { get; init; }
    public uint? LastUid { get; init; }
}

public sealed record EmailFetchResult
{
    public IReadOnlyList<EmailMessage> Messages { get; init; } = [];
    public uint? UidValidity { get; init; }
    public uint? HighestUid { get; init; }
}

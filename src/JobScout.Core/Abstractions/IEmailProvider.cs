using JobScout.Core.Models;

namespace JobScout.Core.Abstractions;

/// <summary>Read-only mailbox access. Implementations must never move, delete,
/// flag or send anything.</summary>
public interface IEmailProvider
{
    /// <summary>Stable key identifying the mailbox, used for the checkpoint row.</summary>
    string MailboxKey { get; }

    bool IsConfigured { get; }

    Task<EmailFetchResult> FetchSinceAsync(EmailFetchCursor cursor, CancellationToken ct = default);
}

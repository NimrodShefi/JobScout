namespace JobScout.Core.Entities;

/// <summary>Remembers how far the email reader got, so each run only fetches new messages.
/// Holds no message content.</summary>
public class EmailCheckpoint
{
    public int Id { get; set; }

    /// <summary>Mailbox identity, e.g. "imap:me@example.com/INBOX".</summary>
    public string MailboxKey { get; set; } = string.Empty;

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>IMAP UIDVALIDITY - if this changes, UIDs are meaningless and we fall back to dates.</summary>
    public uint? UidValidity { get; set; }

    /// <summary>Highest IMAP UID already processed.</summary>
    public uint? LastUid { get; set; }
}

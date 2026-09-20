using JobScout.Core.Abstractions;
using JobScout.Core.Models;
using JobScout.Core.Options;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure.Email;

/// <summary>Generic IMAP reader. Opens the folder read-only, fetches only messages newer
/// than the last checkpoint, and returns a truncated body snippet. It never marks, moves,
/// deletes or sends anything - the folder is opened with FolderAccess.ReadOnly, so the
/// server itself will refuse a write.
///
/// A Gmail API or Microsoft Graph provider would implement this same interface and slot in
/// behind the same configuration switch.</summary>
public sealed class ImapEmailProvider(
    IOptions<JobScoutOptions> options,
    ILogger<ImapEmailProvider> logger) : IEmailProvider
{
    private EmailOptions Email => options.Value.Email;
    private ImapOptions Imap => Email.Imap;

    public string MailboxKey =>
        $"imap:{Imap.Username ?? "unknown"}@{Imap.Host ?? "unknown"}/{Imap.Folder}";

    public bool IsConfigured =>
        Email.Provider == EmailProviderKind.Imap &&
        !string.IsNullOrWhiteSpace(Imap.Host) &&
        !string.IsNullOrWhiteSpace(Imap.Username) &&
        !string.IsNullOrWhiteSpace(Imap.Password);

    public async Task<EmailFetchResult> FetchSinceAsync(EmailFetchCursor cursor, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("IMAP is not configured - nothing to read");
            return new EmailFetchResult();
        }

        // IsConfigured above guarantees these, but pulling them out says so to the compiler
        // and keeps the connect call readable.
        var host = Imap.Host!;
        var username = Imap.Username!;
        var password = Imap.Password!;

        using var client = new ImapClient();

        await client.ConnectAsync(
            host,
            Imap.Port,
            Imap.UseSsl ? MailKit.Security.SecureSocketOptions.SslOnConnect
                        : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable,
            ct);

        await client.AuthenticateAsync(username, password, ct);

        var folder = Imap.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(Imap.Folder, ct);

        // Read-only: the server rejects any attempt to change flags or move messages.
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uidValidity = folder.UidValidity;

        // UIDs are only comparable while UIDVALIDITY is unchanged. When it changes the
        // mailbox has been rebuilt, so fall back to a date search.
        var uidsUsable = cursor.UidValidity is not null &&
                         cursor.UidValidity == uidValidity &&
                         cursor.LastUid is not null;

        IList<UniqueId> uids;

        if (uidsUsable)
        {
            var range = new UniqueIdRange(new UniqueId(cursor.LastUid!.Value + 1), UniqueId.MaxValue);
            uids = await folder.SearchAsync(range, SearchQuery.All, ct);
        }
        else
        {
            var since = cursor.Since ?? DateTimeOffset.UtcNow.AddDays(-Math.Max(1, Email.InitialLookbackDays));
            logger.LogInformation("IMAP UIDs unusable or first run - searching by date since {Since:u}", since);
            uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime), ct);
        }

        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return new EmailFetchResult { UidValidity = uidValidity, HighestUid = cursor.LastUid };
        }

        // Newest last; cap the batch so one long absence cannot blow up a run.
        var batch = uids.OrderBy(u => u.Id).TakeLast(Math.Max(1, Email.MaxMessagesPerRun)).ToList();

        if (batch.Count < uids.Count)
            logger.LogWarning("IMAP found {Total} new message(s); processing the most recent {Count}",
                uids.Count, batch.Count);

        var messages = new List<EmailMessage>(batch.Count);

        foreach (var uid in batch)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var message = await folder.GetMessageAsync(uid, ct);

                var from = message.From.Mailboxes.FirstOrDefault();
                if (from is null) continue;

                messages.Add(new EmailMessage
                {
                    MessageId = message.MessageId ?? $"{MailboxKey}#{uid.Id}",
                    Uid = uid.Id,
                    FromName = from.Name,
                    FromAddress = from.Address,
                    Subject = message.Subject,
                    ReceivedAt = message.Date,
                    BodySnippet = Snippet(message),
                });
            }
            catch (Exception ex)
            {
                // Identify by UID only - never echo the message content into the logs.
                logger.LogWarning("Could not read IMAP message uid {Uid}: {Error}", uid.Id, ex.Message);
            }
        }

        await client.DisconnectAsync(true, ct);

        // Count only. Senders, subjects and bodies stay out of the log files.
        logger.LogInformation("Read {Count} new message(s) from {Mailbox}", messages.Count, MailboxKey);

        return new EmailFetchResult
        {
            Messages = messages,
            UidValidity = uidValidity,
            HighestUid = batch[^1].Id,
        };
    }

    /// <summary>Plain text where possible, HTML stripped otherwise, truncated to the
    /// configured length. This snippet goes to the AI and is then discarded.</summary>
    private string? Snippet(MimeKit.MimeMessage message)
    {
        var text = message.TextBody;

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(message.HtmlBody))
            text = Fetching.HtmlText.ExtractText(message.HtmlBody, Email.BodySnippetChars * 4);

        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        var max = Math.Max(200, Email.BodySnippetChars);

        return text.Length <= max ? text : text[..max];
    }
}

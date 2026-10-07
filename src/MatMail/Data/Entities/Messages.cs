namespace MatMail.Data;

/// <summary>
/// Metadata of one message in one folder. The raw RFC 822 bytes live in <see cref="MailMessageContent"/>, so list
/// queries never touch the (potentially large) body.
/// </summary>
public class MailMessage : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long MailboxId { get; set; }
    public long FolderId { get; set; }
    public MailFolder? Folder { get; set; }

    /// <summary>IMAP UID, unique and ascending within the folder.</summary>
    public long Uid { get; set; }
    public long ModSeq { get; set; }

    public string? MessageIdHeader { get; set; }
    public string? InReplyTo { get; set; }
    public string? ReferencesHeader { get; set; }

    /// <summary>Groups a conversation (root Message-ID, or the normalized subject as fallback).</summary>
    public string? ThreadKey { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Display text of the To/Cc recipients, for lists and search.</summary>
    public string ToSummary { get; set; } = string.Empty;
    public DateTime? SentDate { get; set; }

    /// <summary>IMAP INTERNALDATE: when the message arrived here.</summary>
    public DateTime ReceivedDate { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>First characters of the text body (the "snippet" in the list).</summary>
    public string Preview { get; set; } = string.Empty;

    public bool IsRead { get; set; }
    public bool IsStarred { get; set; }
    public bool IsAnswered { get; set; }
    public bool IsForwarded { get; set; }
    public bool IsDraft { get; set; }

    /// <summary>IMAP \Deleted: marked, removed by EXPUNGE.</summary>
    public bool IsDeleted { get; set; }
    public bool HasAttachments { get; set; }

    /// <summary>Additional IMAP keywords/labels.</summary>
    public string[] Keywords { get; set; } = Array.Empty<string>();

    public MessageStorage Storage { get; set; } = MessageStorage.Local;

    /// <summary>The connected account that delivered this message (null: SMTP, webmail, IMAP APPEND).</summary>
    public long? SourceAccountId { get; set; }
    public string? RemoteFolder { get; set; }
    public string? RemoteUid { get; set; }

    /// <summary>The addresses this copy was routed for (SMTP RCPT TO / catch-all analysis); kept to explain "why is this here".</summary>
    public string? EnvelopeRecipients { get; set; }

    public MailMessageContent? Content { get; set; }
}

public class MailMessageContent : AuditedEntity
{
    public long MessageId { get; set; }
    public MailMessage? Message { get; set; }

    /// <summary>The complete message as received. Null while <see cref="MailMessage.Storage"/> is <see cref="MessageStorage.Remote"/>.</summary>
    public byte[]? Raw { get; set; }

    /// <summary>Only the header block, so ENVELOPE/HEADER fetches do not load <see cref="Raw"/>.</summary>
    public byte[]? HeaderBytes { get; set; }

    /// <summary>Plain text of the body (truncated), searched by the body search.</summary>
    public string? SearchText { get; set; }

    /// <summary>Cached IMAP ENVELOPE / BODYSTRUCTURE strings (filled lazily by the IMAP server).</summary>
    public string? EnvelopeImap { get; set; }
    public string? BodyStructureImap { get; set; }
}

/// <summary>A message waiting to be delivered to external recipients (through a provider's SMTP or directly).</summary>
public class OutboundMessage : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long? MailboxId { get; set; }
    public long? SenderUserId { get; set; }

    /// <summary>The connected account whose SMTP delivers this message. Null = direct delivery via MX lookup.</summary>
    public long? MailAccountId { get; set; }
    public MailAccount? MailAccount { get; set; }

    public string EnvelopeFrom { get; set; } = string.Empty;
    public string[] Recipients { get; set; } = Array.Empty<string>();
    public string Subject { get; set; } = string.Empty;
    public byte[] Raw { get; set; } = Array.Empty<byte>();
    public long SizeBytes { get; set; }

    public OutboundStatus Status { get; set; } = OutboundStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime NextAttemptDate { get; set; }
    public DateTime? SentDate { get; set; }
    public string? LastError { get; set; }
}

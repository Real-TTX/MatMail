namespace MatMail.Data;

/// <summary>
/// One line of the mail transfer log: a message that came into the server, went out of it or moved between mailboxes, with the door it
/// used and how it ended. Written next to the activity log, but one line per message, so "what came in and what went out" can be looked up.
/// An outgoing message has one line that follows it through the queue (queued, deferred, delivered or failed).
/// </summary>
public class MailTransfer : AuditedEntity
{
    /// <summary>The tenant the message belongs to (null when nobody could be found, e.g. mail refused for an unknown domain).</summary>
    public long? TenantId { get; set; }

    public TransferDirection Direction { get; set; }
    public TransferChannel Channel { get; set; }
    public TransferStatus Status { get; set; }

    public string? MessageIdHeader { get; set; }

    /// <summary>Empty when the log is set not to keep subjects.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The envelope sender (or the From address when there was none).</summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>The recipients, separated by commas; long lists are cut (<see cref="RecipientCount"/> says how many there were).</summary>
    public string Recipients { get; set; } = string.Empty;
    public int RecipientCount { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Who or what was on the other end: a connected account, the signed-in user, a smart-host rule, the receiving server.</summary>
    public string? Peer { get; set; }
    public string? RemoteIp { get; set; }

    /// <summary>What happened: the mailboxes that got the message, the answer of the receiving server, the reason of a refusal.</summary>
    public string? Detail { get; set; }

    /// <summary>The queue entry of an outgoing message (it may be gone by now: queue entries are cleaned up earlier than the log).</summary>
    public long? OutboundMessageId { get; set; }

    /// <summary>How many delivery attempts an outgoing message has had.</summary>
    public int Attempts { get; set; }
}

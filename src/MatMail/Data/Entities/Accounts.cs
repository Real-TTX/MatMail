namespace MatMail.Data;

/// <summary>
/// A connected provider account (Strato, Netcup, ...): where mail is fetched from and/or sent through. The gateway hands out its
/// own credentials, so the provider's data never leaves the server and a provider move only changes this record.
/// </summary>
public class MailAccount : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }

    /// <summary>Display name, e.g. "Strato – info@example.com".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The provider mailbox's own address (also the sender used for SMTP).</summary>
    public string Address { get; set; } = string.Empty;
    public MailAccountRole Role { get; set; } = MailAccountRole.Mail;
    public bool IsEnabled { get; set; } = true;

    /// <summary>The provider mailbox collects the mail of a whole domain: recipients are taken from the headers, not assumed to be <see cref="Address"/>.</summary>
    public bool IsCatchAll { get; set; }

    /// <summary>
    /// Role Mail: fallback mailbox for mail nobody else claims (blank = tenant "Unassigned").
    /// Role Backup / Migration: the mailbox the copies are stored in.
    /// </summary>
    public long? TargetMailboxId { get; set; }
    public Mailbox? TargetMailbox { get; set; }
    public ServerRetention Retention { get; set; } = ServerRetention.KeepOnServer;

    // ----- Receiving (IMAP / POP3) -----
    public ReceiveProtocol ReceiveProtocol { get; set; } = ReceiveProtocol.Imap;
    public string? ReceiveHost { get; set; }
    public int ReceivePort { get; set; } = 993;
    public ConnectionSecurity ReceiveSecurity { get; set; } = ConnectionSecurity.Ssl;
    public string? ReceiveUsername { get; set; }

    /// <summary>Encrypted with the data-protection keys of this installation.</summary>
    public string? ReceivePasswordProtected { get; set; }

    /// <summary>Accept a certificate that is self-signed or does not match (only for servers inside your own network).</summary>
    public bool AllowInvalidCertificate { get; set; }

    // ----- Sending (SMTP) -----
    public string? SendHost { get; set; }
    public int SendPort { get; set; } = 587;
    public ConnectionSecurity SendSecurity { get; set; } = ConnectionSecurity.StartTls;
    public bool SendUsesReceiveCredentials { get; set; } = true;
    public string? SendUsername { get; set; }
    public string? SendPasswordProtected { get; set; }

    // ----- Synchronisation -----
    public int SyncIntervalMinutes { get; set; } = 5;

    /// <summary>Remote folders to fetch (IMAP). Ignored when <see cref="SyncAllFolders"/> is set.</summary>
    public string[] SyncFolders { get; set; } = new[] { "INBOX" };
    public bool SyncAllFolders { get; set; }
    public SyncState LastSyncState { get; set; } = SyncState.Never;
    public DateTime? LastSyncDate { get; set; }
    public string? LastSyncMessage { get; set; }
    public DateTime? NextSyncDate { get; set; }
    public int FailureCount { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Per remote folder: where the last synchronisation stopped (IMAP UIDVALIDITY / highest UID).</summary>
public class MailAccountFolderState : AuditedEntity
{
    public long MailAccountId { get; set; }
    public MailAccount? MailAccount { get; set; }
    public string RemoteFolder { get; set; } = string.Empty;
    public long UidValidity { get; set; }
    public long LastUid { get; set; }
    public long? LocalFolderId { get; set; }
    public DateTime? LastSyncDate { get; set; }
}

/// <summary>Remembers which provider message was already fetched (IMAP UID per folder, POP3 UIDL), so nothing is downloaded twice.</summary>
public class RemoteMessageState : AuditedEntity
{
    public long MailAccountId { get; set; }
    public MailAccount? MailAccount { get; set; }
    public string RemoteFolder { get; set; } = string.Empty;
    public string RemoteUid { get; set; } = string.Empty;

    /// <summary>The local copy; null when the message was fetched but not stored (e.g. nothing to deliver).</summary>
    public long? LocalMessageId { get; set; }
}

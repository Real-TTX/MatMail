namespace MatMail.Data;

/// <summary>A mail domain a tenant owns (globally unique). Recipient addresses are only accepted for known domains.</summary>
public class Domain : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }

    /// <summary>Lower-case, e.g. "example.com".</summary>
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>Where mail for unknown addresses of this domain goes. Null = the tenant's "Unassigned" mailbox.</summary>
    public long? CatchAllMailboxId { get; set; }
    public Mailbox? CatchAllMailbox { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// A place where mail lives: a user's personal mailbox, a shared/public mailbox (e.g. info@, served by several users)
/// or the tenant's "Unassigned" bucket for mail nobody could be found for.
/// </summary>
public class Mailbox : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public MailboxType Type { get; set; } = MailboxType.Personal;

    /// <summary>The owner of a personal mailbox (full access implicitly).</summary>
    public long? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }

    /// <summary>
    /// The most the messages of this mailbox may take up on this server, in bytes (see <c>MailboxUsageService</c>); null = no limit.
    /// Mail that would not fit is not accepted (see <c>MailboxQuota</c>); deleting is always possible.
    /// </summary>
    public long? QuotaBytes { get; set; }

    public List<MailboxAlias> Aliases { get; set; } = new();
    public List<MailboxPermission> Permissions { get; set; } = new();
    public List<MailFolder> Folders { get; set; } = new();
}

/// <summary>
/// An address that routes into a mailbox (inbound) and that the mailbox may send as (outbound).
/// <c>*@domain.tld</c> is a catch-all for that domain.
/// </summary>
public class MailboxAlias : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }

    /// <summary>Lower-case, globally unique.</summary>
    public string Address { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public bool CanSend { get; set; } = true;

    /// <summary>Connected provider account whose SMTP is used when sending as this address. Null = tenant default / direct delivery.</summary>
    public long? SendAccountId { get; set; }
    public MailAccount? SendAccount { get; set; }

    public bool IsCatchAll => Address.StartsWith("*@", StringComparison.Ordinal);
}

/// <summary>Delegation: <see cref="User"/> may work in somebody else's / a shared mailbox up to <see cref="Access"/>.</summary>
public class MailboxPermission : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public MailboxAccess Access { get; set; } = MailboxAccess.Read;
}

public class MailFolder : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }
    public long? ParentId { get; set; }
    public MailFolder? Parent { get; set; }

    /// <summary>Leaf name; never contains "/". The full path is built from the parents.</summary>
    public string Name { get; set; } = string.Empty;
    public FolderKind Kind { get; set; } = FolderKind.Custom;

    /// <summary>IMAP UIDVALIDITY: changes only when UIDs of this folder are reassigned.</summary>
    public long UidValidity { get; set; }

    /// <summary>IMAP UIDNEXT: the UID the next message of this folder receives.</summary>
    public long UidNext { get; set; } = 1;

    /// <summary>Bumped on every change (new/flag/expunge), so sessions can cheaply see whether anything happened.</summary>
    public long ModSeq { get; set; }
    public bool IsSubscribed { get; set; } = true;
}

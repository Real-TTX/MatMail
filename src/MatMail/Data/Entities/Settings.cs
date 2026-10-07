namespace MatMail.Data;

/// <summary>
/// A signature template. Placeholders: {{DisplayName}}, {{Email}}, {{JobTitle}}, {{Phone}}, {{Tenant}}.
/// <see cref="SignatureKind.Signature"/> is inserted by the mail client, <see cref="SignatureKind.Footer"/> is appended by the server.
/// </summary>
public class Signature : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SignatureScope Scope { get; set; } = SignatureScope.Tenant;
    public SignatureKind Kind { get; set; } = SignatureKind.Signature;

    /// <summary>For <see cref="SignatureScope.Mailbox"/>.</summary>
    public long? MailboxId { get; set; }

    /// <summary>For <see cref="SignatureScope.User"/>.</summary>
    public long? UserId { get; set; }
    public string Html { get; set; } = string.Empty;
    public string? PlainText { get; set; }

    /// <summary>The signature that is pre-selected when a message is composed.</summary>
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// "Smart host" rule: SMTP clients from this network may relay without signing in.
/// </summary>
public class RelayRule : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>A single IP ("192.168.1.20") or a CIDR range ("192.168.1.0/24", "fd00::/8").</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>Sender domains this network may use. Empty = every domain of the tenant.</summary>
    public string[] AllowedSenderDomains { get; set; } = Array.Empty<string>();

    /// <summary>Provider account used for delivery. Null = pick by sender address / direct delivery.</summary>
    public long? SendAccountId { get; set; }
    public MailAccount? SendAccount { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>Operational log shown in the admin area (sign-in failures, sync results, SMTP/IMAP events, queue).</summary>
public class ActivityLog : AuditedEntity
{
    public long? TenantId { get; set; }
    public long? UserId { get; set; }
    public ActivityCategory Category { get; set; } = ActivityCategory.System;
    public ActivityLevel Level { get; set; } = ActivityLevel.Info;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? RemoteIp { get; set; }
}

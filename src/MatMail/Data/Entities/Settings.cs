namespace MatMail.Data;

/// <summary>
/// A signature template. Placeholders: {{DisplayName}}, {{Email}}, {{JobTitle}}, {{Phone}}, {{Tenant}}.
/// <see cref="SignatureKind.Signature"/> is inserted by the mail client, <see cref="SignatureKind.Footer"/> is appended by the server.
/// </summary>
public class Signature : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public AppliesTo Scope { get; set; } = AppliesTo.Tenant;
    public SignatureKind Kind { get; set; } = SignatureKind.Signature;

    /// <summary>For <see cref="AppliesTo.Mailbox"/>.</summary>
    public long? MailboxId { get; set; }

    /// <summary>For <see cref="AppliesTo.User"/>.</summary>
    public long? UserId { get; set; }
    public string Html { get; set; } = string.Empty;
    public string? PlainText { get; set; }

    /// <summary>The signature that is pre-selected when a message is composed.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// For a signature (not a footer): the server also adds it to messages that were written without it, as it happens in mail programs
    /// such as Outlook. Messages written in the web client carry the signature the writer chose and are left alone.
    /// </summary>
    public bool AddOnServer { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A frame for outgoing mail: the message is put into the HTML of the template at {{Body}}. The main use is a message that only has
/// plain text (devices, scripts and simple SMTP clients through the smart host) and should leave as a proper HTML mail in the look of
/// the company. Chosen by rule: who sends (tenant, mailbox, user), where the message comes from and which kind of message it is.
/// </summary>
public class MailTemplate : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public AppliesTo Scope { get; set; } = AppliesTo.Tenant;

    /// <summary>For <see cref="AppliesTo.Mailbox"/>.</summary>
    public long? MailboxId { get; set; }

    /// <summary>For <see cref="AppliesTo.User"/>.</summary>
    public long? UserId { get; set; }

    /// <summary>Messages written in the web client.</summary>
    public bool ForWebClient { get; set; }

    /// <summary>Messages of mail programs (Outlook, Thunderbird, phones) that signed in to the SMTP server.</summary>
    public bool ForMailPrograms { get; set; } = true;

    /// <summary>Messages of devices and servers in a trusted network (smart host).</summary>
    public bool ForSmartHost { get; set; } = true;

    /// <summary>Only messages that came in through this smart-host rule; null: through any.</summary>
    public long? RelayRuleId { get; set; }

    public TemplateMode Mode { get; set; } = TemplateMode.PlainTextOnly;

    /// <summary>The HTML with {{Body}} where the message goes; the placeholders of signatures can be used as well.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>Of several templates that fit, the lowest number wins (after the more specific scope).</summary>
    public int Priority { get; set; } = 100;
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

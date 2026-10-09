namespace MatMail.Data;

// All enums are stored as their name (text) in PostgreSQL, so the tables stay readable.

/// <summary>Kind of a mailbox: a user's own mailbox, a shared/public one (e.g. info@) or the tenant's "Unassigned" bucket.</summary>
public enum MailboxType
{
    Personal,
    Shared,
    Unassigned,
}

/// <summary>IMAP special-use role of a folder (RFC 6154).</summary>
public enum FolderKind
{
    Custom,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
}

/// <summary>What a user may do in somebody else's (or a shared) mailbox. Higher includes lower.</summary>
public enum MailboxAccess
{
    Read = 1,
    Edit = 2,
    Send = 3,
    Manage = 4,
}

/// <summary>Why a connected provider account exists.</summary>
public enum MailAccountRole
{
    /// <summary>Everyday mail: fetched mail is distributed to the mailboxes, the account's SMTP can be used for sending.</summary>
    Mail,
    /// <summary>Copies everything into a backup mailbox; nothing is distributed and nothing is ever deleted at the provider.</summary>
    Backup,
    /// <summary>Moves a whole account (all folders) from an old provider into a mailbox.</summary>
    Migration,
    /// <summary>Only used as outgoing relay (SMTP); nothing is fetched.</summary>
    SendOnly,
}

/// <summary>What happens with messages on the provider's server.</summary>
public enum ServerRetention
{
    /// <summary>Download a copy, leave the original on the server.</summary>
    KeepOnServer,
    /// <summary>Download, then delete the original on the server.</summary>
    DeleteAfterDownload,
    /// <summary>Do not store bodies locally: list from the provider in near real time and fetch bodies on demand.</summary>
    LiveAccess,
}

public enum ReceiveProtocol
{
    None,
    Imap,
    Pop3,
}

public enum ConnectionSecurity
{
    None,
    StartTls,
    Ssl,
}

public enum SyncState
{
    Never,
    Running,
    Ok,
    Error,
}

public enum OutboundStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    Cancelled,
}

/// <summary>Where the raw bytes of a message live.</summary>
public enum MessageStorage
{
    Local,
    /// <summary>Only metadata is stored; the body is fetched from the provider when needed (<see cref="ServerRetention.LiveAccess"/>).</summary>
    Remote,
}

/// <summary>Whom a signature, footer or mail template applies to.</summary>
public enum AppliesTo
{
    Tenant,
    Mailbox,
    User,
}

/// <summary>Which messages a mail template is put around.</summary>
public enum TemplateMode
{
    /// <summary>Only messages that have no HTML version (what devices, scripts and simple SMTP clients send): they get one.</summary>
    PlainTextOnly,

    /// <summary>Every message: an HTML version is put into the template, a plain-text one gets an HTML version first.</summary>
    AllMessages,
}

public enum SignatureKind
{
    /// <summary>Inserted by the mail client when composing; the user may edit or remove it.</summary>
    Signature,
    /// <summary>Appended by the server to every outgoing message in its scope; cannot be removed by the sender.</summary>
    Footer,
}

/// <summary>Whether a tenant makes two-factor authentication mandatory (roles can require it for their members on top of this).</summary>
public enum TwoFactorMode
{
    /// <summary>Everybody decides for themselves.</summary>
    Optional,

    /// <summary>Users who hold any permission other than using mail, and system administrators.</summary>
    Administrators,

    /// <summary>Every user of the tenant.</summary>
    Everyone,
}

public enum ActivityCategory
{
    System,
    Auth,
    Admin,
    Smtp,
    Imap,
    Sync,
    Queue,
}

public enum ActivityLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Whether a mailbox rule needs all of its conditions or one of them.</summary>
public enum RuleMatch
{
    All,
    Any,
}

/// <summary>What a rule condition looks at.</summary>
public enum RuleField
{
    /// <summary>Name and address of the sender.</summary>
    From,

    /// <summary>The recipients: To, Cc and the addresses the message was routed for.</summary>
    To,
    Subject,

    /// <summary>The text of the message.</summary>
    Body,

    /// <summary>A header field of the message; its name is the condition's <c>HeaderName</c>.</summary>
    Header,

    /// <summary>Whether the message has attachments ("true" or "false").</summary>
    HasAttachment,

    /// <summary>The size of the message in kilobytes.</summary>
    Size,
}

public enum RuleOperator
{
    Contains,
    DoesNotContain,
    Equals,
    StartsWith,
    EndsWith,
    LargerThan,
    SmallerThan,
}

/// <summary>What a rule does with a message that matches.</summary>
public enum RuleActionType
{
    /// <summary>Puts the message into another folder of the mailbox instead of the inbox.</summary>
    MoveToFolder,
    MarkAsRead,

    /// <summary>Stars (flags) the message.</summary>
    Star,

    /// <summary>Gives the message a label (an IMAP keyword).</summary>
    AddLabel,

    /// <summary>Puts the message into the trash.</summary>
    MoveToTrash,

    /// <summary>Deletes the message for good, without trash.</summary>
    Discard,

    /// <summary>Sends a copy to another address; the message itself stays.</summary>
    ForwardTo,
}

/// <summary>Which way a message went through the server.</summary>
public enum TransferDirection
{
    /// <summary>Into the server from outside: from another mail server or a connected provider account.</summary>
    Inbound,

    /// <summary>Out of the server to external recipients.</summary>
    Outbound,

    /// <summary>Between mailboxes of this server.</summary>
    Internal,
}

/// <summary>The door a message came through or left by.</summary>
public enum TransferChannel
{
    /// <summary>Another mail server delivering to us (SMTP, port 25).</summary>
    SmtpServer,

    /// <summary>A mail program that signed in to the SMTP server.</summary>
    SmtpSubmission,

    /// <summary>A device or server of a trusted network, without signing in.</summary>
    SmartHost,
    WebClient,

    /// <summary>Fetched from a connected provider account.</summary>
    ProviderAccount,

    /// <summary>Forwarded by a mailbox rule.</summary>
    Rule,

    /// <summary>The server itself, e.g. a bounce.</summary>
    System,
}

public enum TransferStatus
{
    Delivered,

    /// <summary>Waiting in the outgoing queue.</summary>
    Queued,

    /// <summary>The first attempts failed for now; the server tries again.</summary>
    Deferred,
    Failed,

    /// <summary>The server refused the message.</summary>
    Rejected,

    /// <summary>Accepted, but deleted at once by a mailbox rule.</summary>
    Discarded,
}

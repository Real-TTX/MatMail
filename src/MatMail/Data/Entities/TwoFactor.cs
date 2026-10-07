namespace MatMail.Data;

/// <summary>
/// The authenticator app of a user (a time-based one-time password, RFC 6238). One row per user. While
/// <see cref="ConfirmedDate"/> is empty the enrolment is only started (the secret is shown, no code was entered yet) and the
/// user signs in without a second step.
/// </summary>
public class UserTotp : AuditedEntity
{
    public long UserId { get; set; }

    /// <summary>The Base32 secret, encrypted with the data protection keys (<see cref="MatMail.Services.SecretProtector"/>).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>When the user proved with a first code that the app works; null = enrolment pending, two-factor authentication is off.</summary>
    public DateTime? ConfirmedDate { get; set; }

    /// <summary>The 30 second step of the code accepted last. Codes of this step or an earlier one are refused: no code works twice.</summary>
    public long LastUsedStep { get; set; }
}

/// <summary>A one-time code for the day the phone is gone. Only the hash is stored; the codes are shown once, when they are created.</summary>
public class UserRecoveryCode : AuditedEntity
{
    public long UserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Set when the code was used; a used code is worthless.</summary>
    public DateTime? UsedDate { get; set; }
}

/// <summary>
/// A password for mail programs (IMAP, SMTP) that cannot ask for a second factor. Random, shown once when it is created,
/// named after the device, usable for IMAP and SMTP only and never on the web sign-in page.
/// </summary>
public class AppPassword : AuditedEntity
{
    /// <summary>What the forms carry instead of the numeric id.</summary>
    public Guid Token { get; set; }

    public long UserId { get; set; }

    /// <summary>"Thunderbird on the laptop": chosen by the user.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The first characters of the password: they find the row at sign-in (so one hash is verified, not all) and tell the passwords apart in the list. Not secret.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash (same format as the account password).</summary>
    public string SecretHash { get; set; } = string.Empty;

    public DateTime? LastUsedDate { get; set; }
    public string? LastUsedIp { get; set; }
}

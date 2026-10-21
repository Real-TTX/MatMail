namespace MatMail.Data;

/// <summary>A Mandant: "Home" for the family, "EineFirma GmbH" for a hosting customer. Everything else belongs to one tenant.</summary>
public class Tenant : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Lower-case, URL-safe name of the tenant's own sign-in address (/t/slug); null = none. Unique across the installation.</summary>
    public string? Slug { get; set; }

    /// <summary>Who of this tenant has to sign in with a second factor. System administrators follow the policy of the tenant they belong to.</summary>
    public TwoFactorMode TwoFactorMode { get; set; } = TwoFactorMode.Optional;
}

/// <summary>How a tenant presents itself: the name and logo in the header and on its sign-in page, its accent colour, its web address.</summary>
public class TenantBranding : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }

    /// <summary>Shown instead of "MatMail" in the header; null = no branding of the name.</summary>
    public string? BrandName { get; set; }

    /// <summary>The company's web address; available to signatures as {{Website}}.</summary>
    public string? Website { get; set; }

    /// <summary>"#rrggbb"; null = the installation's accent colour (or the user's own choice).</summary>
    public string? AccentColor { get; set; }

    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }

    /// <summary>New with every upload: the unguessable address of the logo and, at the same time, what busts the browser's cache.</summary>
    public Guid? LogoToken { get; set; }
}

/// <summary>A person who can sign in (web, IMAP, SMTP). Belongs to exactly one tenant.</summary>
public class User : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }

    /// <summary>Lower-case, globally unique. May be an e-mail address (hosting customers sign in with their mail address).</summary>
    public string LoginName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Contact address for notifications; not used for sign-in.</summary>
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Operator of this installation: manages tenants, the server settings and may switch into every tenant.</summary>
    public bool IsSystemAdmin { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>Profile fields that feed the signature placeholders.</summary>
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }

    /// <summary>How the person is addressed ("Mr", "Ms", "Herr", "Frau", ...).</summary>
    public string? Salutation { get; set; }

    /// <summary>Academic or professional title ("Dr.", "Prof. Dr.").</summary>
    public string? Title { get; set; }

    /// <summary>Optional; without them the placeholders split the display name.</summary>
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Department { get; set; }
    public string? Mobile { get; set; }
    public string? Fax { get; set; }

    /// <summary>"system", "light" or "dark"; null = installation default.</summary>
    public string? ThemeMode { get; set; }
    public string? ThemeAccent { get; set; }
    public string? Culture { get; set; }

    /// <summary>"small", "normal" or "large"; null = normal.</summary>
    public string? TextSize { get; set; }

    /// <summary>"comfortable" or "compact"; null = comfortable.</summary>
    public string? Density { get; set; }

    /// <summary>Time zone for dates and times (IANA id, e.g. "Europe/Berlin"); null = that of the server.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Show the first words of a message next to its subject in the list.</summary>
    public bool ShowPreviews { get; set; } = true;

    /// <summary>Where the reader sits in the mail client: "right" of the list, "below" it; null = a message opens in place of the list.</summary>
    public string? ReadingPane { get; set; }

    /// <summary>The list shows conversations (all messages of a thread as one row) instead of single messages.</summary>
    public bool ConversationView { get; set; }

    /// <summary>The directory (Active Directory, LDAP) that signs this person in; null: a local user with a password of their own.</summary>
    public long? DirectoryId { get; set; }
    public DirectoryConnection? DirectoryConnection { get; set; }

    /// <summary>The distinguished name of the entry in the directory as last seen (it changes when the person is moved there) and its stable id.</summary>
    public string? DirectoryDn { get; set; }
    public string? DirectoryUid { get; set; }

    /// <summary>
    /// Set when a comparison with the directory found the person gone, disabled or no longer allowed: they cannot sign in until the directory
    /// lets them in again (the next comparison clears it).
    /// </summary>
    public DateTime? DirectoryDisabledDate { get; set; }

    /// <summary>
    /// The claim <c>sub</c> of the person at MatIdentity (the sign-in of the Mat platform, on when <c>MATIDENTITY_AUTHORITY</c> is set): the person
    /// is found by it at every sign-in there. Null: never signed in through MatIdentity. Only the web sign-in uses it; IMAP and SMTP keep
    /// taking the password or an app password of this user.
    /// </summary>
    public string? ExternalSubject { get; set; }

    public DateTime? LastLoginDate { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilDate { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
}

/// <summary>
/// A server-side session. The cookie only carries <see cref="Token"/>; the row decides whether it is still valid,
/// so sessions survive container restarts and can be revoked.
/// </summary>
public class UserSession : AuditedEntity
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The tenant the user is working in right now (system admins can switch it).</summary>
    public long TenantId { get; set; }
    public DateTime ExpiresDate { get; set; }
    public DateTime LastSeenDate { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity: the sign-out ends the session there too with it (<c>id_token_hint</c>). Null for every other sign-in.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>
    /// The session at MatIdentity this session came from (its claim <c>sid</c>): a back-channel logout that names it ends this session
    /// (<c>IdentitySessionService</c>). Null for every other sign-in, and when MatIdentity sent none.
    /// </summary>
    public string? IdentitySessionId { get; set; }
}

/// <summary>A named set of permissions inside a tenant (see <see cref="MatMail.Services.Permissions"/>).</summary>
public class Role : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Built-in roles can be edited but not deleted.</summary>
    public bool IsBuiltIn { get; set; }
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>Everybody who holds this role must sign in with a second factor.</summary>
    public bool RequiresTwoFactor { get; set; }
}

public class UserRole : AuditedEntity
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public long RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>
/// The person belongs to this tenant in addition to the one that created them (<see cref="User.TenantId"/>, the home tenant, which has no row
/// here). A row is an active membership and exists only because the person accepted an invitation. The roles of a membership are
/// <see cref="UserRole"/> rows of roles of this tenant.
/// </summary>
public class TenantMembership : AuditedEntity, ITenantEntity
{
    /// <summary>The tenant that is joined.</summary>
    public long TenantId { get; set; }

    public long UserId { get; set; }
    public User? User { get; set; }
}

/// <summary>
/// An administrator of a tenant asks somebody to join. Kept by <see cref="Identifier"/> and not by user, so it exists whether or not the
/// person does and the administrator learns nothing about who is on the server. The inviter is <see cref="AuditedEntity.CreateUserId"/>.
/// </summary>
public class TenantInvitation : AuditedEntity, ITenantEntity
{
    /// <summary>The inviting tenant.</summary>
    public long TenantId { get; set; }

    /// <summary>A login name or an address, lower case, as the administrator typed it.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Roles of the inviting tenant that the person gets when they accept (those that still exist then).</summary>
    public long[] RoleIds { get; set; } = Array.Empty<long>();

    public DateTime ExpiresDate { get; set; }
    public InvitationState State { get; set; } = InvitationState.Open;
    public DateTime? ClosedDate { get; set; }

    /// <summary>Who accepted or declined; empty while the invitation is open.</summary>
    public long? UserId { get; set; }
}

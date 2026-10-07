namespace MatMail.Data;

/// <summary>A Mandant: "Home" for the family, "EineFirma GmbH" for a hosting customer. Everything else belongs to one tenant.</summary>
public class Tenant : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
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

    /// <summary>"system", "light" or "dark"; null = installation default.</summary>
    public string? ThemeMode { get; set; }
    public string? ThemeAccent { get; set; }
    public string? Culture { get; set; }

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
}

public class UserRole : AuditedEntity
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public long RoleId { get; set; }
    public Role? Role { get; set; }
}

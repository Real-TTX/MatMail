using System.Security.Claims;

namespace MatMail.Services;

/// <summary>
/// Who is acting right now. In a web request this reads the session principal; background work (sync, queue)
/// and protocol sessions (IMAP/SMTP) call <see cref="RunAs"/> / <see cref="RunAsSystem"/> on their own scope.
/// The database context uses it for the audit columns and the tenant filter.
/// </summary>
public sealed class CurrentUser
{
    private readonly IHttpContextAccessor? _http;
    private Actor? _actor;

    public CurrentUser(IHttpContextAccessor? http = null) => _http = http;

    private sealed record Actor(long? UserId, long? TenantId, bool IsSystemAdmin, HashSet<string> Permissions, string? DisplayName);

    private ClaimsPrincipal? Principal => _http?.HttpContext?.User;

    public bool IsAuthenticated => _actor is not null || (Principal?.Identity?.IsAuthenticated ?? false);

    public long? UserId => _actor is { } a ? a.UserId : ReadLong(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The tenant the current user works in. <c>null</c> means "not restricted" (anonymous sign-in pages, background
    /// work); an authenticated principal without a tenant claim gets 0, which matches no row.
    /// </summary>
    public long? TenantId
    {
        get
        {
            if (_actor is { } a)
            {
                return a.TenantId;
            }

            if (Principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return ReadLong(AppClaims.TenantId) ?? 0;
        }
    }

    public long? HomeTenantId => ReadLong(AppClaims.HomeTenantId);
    public string? Username => Principal?.FindFirstValue(ClaimTypes.Name);
    public string? DisplayName => _actor is { } a ? a.DisplayName : Principal?.FindFirstValue(AppClaims.DisplayName);
    public string? TenantName => Principal?.FindFirstValue(AppClaims.TenantName);
    public bool MustChangePassword => Principal?.FindFirstValue(AppClaims.MustChangePassword) == "1";

    /// <summary>The user signs in with a second factor.</summary>
    public bool TwoFactorEnabled => Principal?.FindFirstValue(AppClaims.TwoFactor) == "1";

    /// <summary>The rules of the tenant or a role of the user demand two-factor authentication, and it is not set up yet.</summary>
    public bool TwoFactorSetupRequired => Principal?.FindFirstValue(AppClaims.TwoFactorRequired) == "1" && !TwoFactorEnabled;

    public bool IsSystemAdmin => _actor is { } a ? a.IsSystemAdmin : Principal?.FindFirstValue(AppClaims.SystemAdmin) == "1";

    /// <summary>The permissions the user holds through roles (system administrators additionally hold everything).</summary>
    public IReadOnlySet<string> PermissionSet => _actor is { } a
        ? a.Permissions
        : (Principal?.FindAll(AppClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>());

    /// <summary>True when the user holds the permission (system administrators hold all of them).</summary>
    public bool Can(string permission)
    {
        if (IsSystemAdmin)
        {
            return true;
        }

        if (_actor is { } a)
        {
            return a.Permissions.Contains(permission);
        }

        return Principal?.HasClaim(AppClaims.Permission, permission) ?? false;
    }

    /// <summary>True when the user may open at least one page of the admin area.</summary>
    public bool CanAdminister => IsSystemAdmin || Permissions.All.Any(p => p != Services.Permissions.MailUse && Can(p));

    /// <summary>Acts as the given user (IMAP/SMTP sessions after authentication).</summary>
    public void RunAs(long? userId, long? tenantId, bool isSystemAdmin = false, IEnumerable<string>? permissions = null, string? displayName = null)
        => _actor = new Actor(userId, tenantId, isSystemAdmin, new HashSet<string>(permissions ?? Array.Empty<string>(), StringComparer.Ordinal), displayName);

    /// <summary>Acts as the system: no user, no tenant restriction (background services).</summary>
    public void RunAsSystem() => _actor = new Actor(null, null, true, new HashSet<string>(Services.Permissions.All, StringComparer.Ordinal), "System");

    /// <summary>Acts as the system, restricted to one tenant (e.g. delivering into a tenant's mailboxes).</summary>
    public void RunAsSystemInTenant(long tenantId) => _actor = new Actor(null, tenantId, true, new HashSet<string>(Services.Permissions.All, StringComparer.Ordinal), "System");

    private long? ReadLong(string claimType)
        => long.TryParse(Principal?.FindFirstValue(claimType), out var value) ? value : null;
}

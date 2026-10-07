using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>Where a user stands with two-factor authentication: is it on, and do the rules of the tenant or a role of theirs demand it?</summary>
public sealed record TwoFactorStatus(bool Enabled, bool Required)
{
    public static readonly TwoFactorStatus Off = new(false, false);

    /// <summary>Required but not set up yet: the user has to enrol before anything else.</summary>
    public bool SetupRequired => Required && !Enabled;

    /// <summary>IMAP and SMTP cannot ask for a code, so they take app passwords only.</summary>
    public bool ProtocolsNeedAppPassword => Enabled || Required;
}

/// <summary>How many people of a tenant use two-factor authentication (for the security page).</summary>
public sealed record TwoFactorSummary(int Users, int Enabled, int StillToSetUp);

/// <summary>
/// The rules of "who has to sign in with a second factor": the policy of the tenant (optional, administrators, everyone) and the
/// flag of a role ("members must use it"). System administrators follow the policy of the tenant they belong to, not the one
/// they are working in right now.
/// </summary>
public sealed class TwoFactorPolicy
{
    private readonly MatMailDbContext _db;
    private readonly CurrentUser _current;
    private readonly SessionCache _cache;
    private readonly ActivityLogger _log;

    public TwoFactorPolicy(MatMailDbContext db, CurrentUser current, SessionCache cache, ActivityLogger log)
    {
        _db = db;
        _current = current;
        _cache = cache;
        _log = log;
    }

    /// <summary>The rule itself, free of the database: is a user with these roles bound to the second factor?</summary>
    public static bool IsRequired(TwoFactorMode tenantMode, bool isSystemAdministrator, IEnumerable<string> permissions, bool anyRoleRequires)
        => anyRoleRequires
            || tenantMode == TwoFactorMode.Everyone
            || (tenantMode == TwoFactorMode.Administrators && IsAdministrator(isSystemAdministrator, permissions));

    /// <summary>An administrator holds a permission other than "use mail" (system administrators hold all of them).</summary>
    public static bool IsAdministrator(bool isSystemAdministrator, IEnumerable<string> permissions)
        => isSystemAdministrator || permissions.Any(p => p != Permissions.MailUse && Permissions.IsKnown(p));

    /// <summary>Reads the state of a user from the database: authenticator confirmed, and required by tenant or role.</summary>
    public async Task<TwoFactorStatus> GetStatusAsync(User user, CancellationToken cancel = default)
    {
        bool enabled = await _db.UserTotps.AnyAsync(t => t.UserId == user.Id && t.ConfirmedDate != null, cancel);
        TwoFactorMode mode = await GetModeAsync(user.TenantId, cancel);
        var roles = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => new { ur.Role!.Permissions, ur.Role.RequiresTwoFactor })
            .ToListAsync(cancel);

        bool required = IsRequired(mode, user.IsSystemAdmin, roles.SelectMany(r => r.Permissions), roles.Any(r => r.RequiresTwoFactor));
        return new TwoFactorStatus(enabled, required);
    }

    public async Task<TwoFactorMode> GetModeAsync(long tenantId, CancellationToken cancel = default)
        => await _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.TwoFactorMode).FirstOrDefaultAsync(cancel);

    /// <summary>Changes the policy of a tenant. Open sessions feel it right away: whoever is bound now is sent to the set-up page. Returns an English error text or null.</summary>
    public async Task<string?> SetModeAsync(long tenantId, TwoFactorMode mode, CancellationToken cancel = default)
    {
        if (!_current.Can(Permissions.SecurityManage))
        {
            return "You are not allowed to do this.";
        }

        Tenant? tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancel);
        if (tenant is null)
        {
            return "The tenant does not exist.";
        }

        if (tenant.TwoFactorMode == mode)
        {
            return null;
        }

        tenant.TwoFactorMode = mode;
        await _db.SaveChangesAsync(cancel);
        _cache.Clear();

        await _log.InfoAsync(
            ActivityCategory.Admin, $"The two-factor policy of '{tenant.Name}' was changed to {mode}.", tenantId: tenantId, userId: _current.UserId);
        return null;
    }

    /// <summary>How many users of the tenant use the authenticator and how many are bound to it but have not set it up yet.</summary>
    public async Task<TwoFactorSummary> GetSummaryAsync(long tenantId, CancellationToken cancel = default)
    {
        TwoFactorMode mode = await GetModeAsync(tenantId, cancel);
        var users = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive)
            .Select(u => new { u.Id, u.IsSystemAdmin })
            .ToListAsync(cancel);

        long[] userIds = users.Select(u => u.Id).ToArray();
        HashSet<long> enabled = (await _db.UserTotps.AsNoTracking()
            .Where(t => t.ConfirmedDate != null && userIds.Contains(t.UserId))
            .Select(t => t.UserId)
            .ToListAsync(cancel)).ToHashSet();

        var roles = await _db.UserRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(ur => ur.Role!.TenantId == tenantId)
            .Select(ur => new { ur.UserId, ur.Role!.Permissions, ur.Role.RequiresTwoFactor })
            .ToListAsync(cancel);
        ILookup<long, string[]> permissionsByUser = roles.ToLookup(r => r.UserId, r => r.Permissions);
        HashSet<long> boundByRole = roles.Where(r => r.RequiresTwoFactor).Select(r => r.UserId).ToHashSet();

        int stillToSetUp = users.Count(u =>
            !enabled.Contains(u.Id)
            && IsRequired(mode, u.IsSystemAdmin, permissionsByUser[u.Id].SelectMany(p => p), boundByRole.Contains(u.Id)));
        return new TwoFactorSummary(users.Count, users.Count(u => enabled.Contains(u.Id)), stillToSetUp);
    }
}

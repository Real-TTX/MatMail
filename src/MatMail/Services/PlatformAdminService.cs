using System.Linq.Expressions;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>A platform administrator (<see cref="User.IsSystemAdmin"/>) as the platform lists them, with the tenant they belong to.</summary>
public sealed record PlatformAdministrator(long UserId, long TenantId, string LoginName, string DisplayName, bool IsActive, DateTime? LastLoginDate);

/// <summary>
/// Who administers the platform. That is decided here, on the platform, by a platform administrator – never on the user page of a tenant.
/// A platform administrator is a user of some tenant (their home) and works in every tenant they go into through the platform. The flag is
/// written in a scope of its own that is not bound to a tenant (the guard of the database context refuses rows of other tenants otherwise).
/// </summary>
public sealed class PlatformAdminService(IServiceScopeFactory scopes, CurrentUser current, MatMailDbContext db, SessionCache cache, ActivityLogger log)
{
    private const string NotAllowed = "You are not allowed to do this.";

    public async Task<IReadOnlyList<PlatformAdministrator>> ListAsync()
        => !current.IsSystemAdmin
            ? Array.Empty<PlatformAdministrator>()
            : await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.IsSystemAdmin)
                .OrderBy(u => u.DisplayName).ThenBy(u => u.LoginName)
                .Select(u => new PlatformAdministrator(u.Id, u.TenantId, u.LoginName, u.DisplayName, u.IsActive, u.LastLoginDate))
                .ToListAsync();

    /// <summary>Makes the user with this login name a platform administrator. Returns an English error text or null.</summary>
    public async Task<string?> GrantAsync(string? loginName)
    {
        if (!current.IsSystemAdmin)
        {
            return NotAllowed;
        }

        string login = (loginName ?? string.Empty).Trim().ToLowerInvariant();
        if (login.Length == 0)
        {
            return "Enter the login name of the person.";
        }

        return await ChangeAsync(u => u.LoginName == login, true);
    }

    /// <summary>Takes the platform away from a user (they keep their tenant and their roles there). At least one active platform administrator stays.</summary>
    public async Task<string?> RevokeAsync(long userId)
    {
        if (!current.IsSystemAdmin)
        {
            return NotAllowed;
        }

        if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsSystemAdmin && u.IsActive && u.Id != userId))
        {
            return "There must be at least one active system administrator.";
        }

        return await ChangeAsync(u => u.Id == userId, false);
    }

    private async Task<string?> ChangeAsync(Expression<Func<User, bool>> which, bool administrator)
    {
        using IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(current.UserId, null, true, Permissions.All);
        var inner = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        User? user = await inner.Users.IgnoreQueryFilters().FirstOrDefaultAsync(which);
        if (user is null)
        {
            return "There is no user with that login name.";
        }

        if (user.IsSystemAdmin == administrator)
        {
            return administrator ? "That person is a platform administrator already." : null;
        }

        user.IsSystemAdmin = administrator;
        await inner.SaveChangesAsync();
        cache.InvalidateUser(user.Id);
        await log.InfoAsync(
            ActivityCategory.Admin, administrator ? $"'{user.LoginName}' became a platform administrator." : $"'{user.LoginName}' is no longer a platform administrator.",
            userId: current.UserId);
        return null;
    }
}

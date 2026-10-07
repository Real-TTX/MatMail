using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>Creating and removing tenants. A new tenant gets its built-in roles and its "Unassigned" mailbox.</summary>
public sealed class TenantService
{
    public const string AdministratorRoleName = "Administrator";
    public const string UserRoleName = "User";

    private readonly IServiceScopeFactory _scopes;
    private readonly CurrentUser _current;

    public TenantService(IServiceScopeFactory scopes, CurrentUser current)
    {
        _scopes = scopes;
        _current = current;
    }

    /// <summary>Creates a tenant. Returns the tenant or an error text (English source string).</summary>
    public async Task<(Tenant? Tenant, string? Error)> CreateAsync(string? name, string? description, long? actingUserId = null)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return (null, "Name is required.");
        }

        // Writes to a tenant other than the one the actor works in are refused by the DbContext guard, so
        // this runs in its own unrestricted scope (still attributed to the acting user).
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(actingUserId ?? _current.UserId, null, true, Permissions.All);
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        if (await db.Tenants.AnyAsync(t => t.Name.ToLower() == trimmed.ToLower()))
        {
            return (null, "A tenant with that name already exists.");
        }

        var tenant = new Tenant { Name = trimmed, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        db.Roles.AddRange(
            new Role
            {
                TenantId = tenant.Id,
                Name = AdministratorRoleName,
                Description = "Manages everything inside this tenant.",
                IsBuiltIn = true,
                Permissions = Permissions.All.ToArray(),
            },
            new Role
            {
                TenantId = tenant.Id,
                Name = UserRoleName,
                Description = "Uses mail; no administration.",
                IsBuiltIn = true,
                Permissions = Permissions.UserDefaults.ToArray(),
            });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MailboxService>().GetUnassignedMailboxAsync(tenant.Id);
        return (tenant, null);
    }

    /// <summary>Deletes a tenant with everything in it (users, mailboxes, messages). Returns an error text or null.</summary>
    public async Task<string?> DeleteAsync(long tenantId)
    {
        if (_current.TenantId == tenantId)
        {
            return "You cannot delete the tenant you are working in. Switch to another tenant first.";
        }

        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(_current.UserId, null, true, Permissions.All);
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenantId && u.IsSystemAdmin))
        {
            return "This tenant contains a system administrator. Move or remove that user first.";
        }

        // Large message tables: delete in SQL instead of loading the graph.
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync();
        return null;
    }
}

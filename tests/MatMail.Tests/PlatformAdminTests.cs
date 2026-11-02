using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>Who administers the platform is decided on the platform; a new tenant gets its first administrator of its own.</summary>
public class PlatformAdminTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private User _root = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _root = await _host.CreateUserAsync(_seed.Tenant.Id, "root", isSystemAdmin: true);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IServiceScope As(User user) => _host.Scope(c => c.RunAs(user.Id, user.TenantId, user.IsSystemAdmin, Permissions.All, user.DisplayName));

    private async Task<bool> IsPlatformAdminAsync(long userId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.IsSystemAdmin).FirstAsync();
    }

    [DbFact]
    public async Task A_platform_administrator_gives_and_takes_the_platform_and_one_always_stays()
    {
        Tenant other;
        using (IServiceScope scope = _host.Scope())
        {
            other = (await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Other", null)).Tenant!;
        }

        User outside = await _host.CreateUserAsync(other.Id, "outside");

        using (IServiceScope scope = As(_root))
        {
            var admins = scope.ServiceProvider.GetRequiredService<PlatformAdminService>();
            Assert.Null(await admins.GrantAsync("  OUTSIDE "));                 // a user of another tenant, by login name
            Assert.NotNull(await admins.GrantAsync("outside"));                  // already one
            Assert.NotNull(await admins.GrantAsync("nobody-here"));
            Assert.Equal(new[] { "outside", "root" }, (await admins.ListAsync()).Select(a => a.LoginName).OrderBy(n => n));
        }

        Assert.True(await IsPlatformAdminAsync(outside.Id));
        Assert.Equal(other.Id, (await _host.Scope().ServiceProvider.GetRequiredService<MatMailDbContext>().Users.IgnoreQueryFilters().FirstAsync(u => u.Id == outside.Id)).TenantId);   // their tenant stays

        using (IServiceScope scope = As(_root))
        {
            var admins = scope.ServiceProvider.GetRequiredService<PlatformAdminService>();
            Assert.Null(await admins.RevokeAsync(outside.Id));
            Assert.NotNull(await admins.RevokeAsync(_root.Id));                  // the last one stays
        }

        Assert.False(await IsPlatformAdminAsync(outside.Id));
        Assert.True(await IsPlatformAdminAsync(_root.Id));
    }

    [DbFact]
    public async Task Only_a_platform_administrator_decides_who_administers_the_platform()
    {
        Role administrators = await _host.CreateRoleAsync(_seed.Tenant.Id, "Everything", requiresTwoFactor: false, Permissions.All.ToArray());
        User tenantAdmin = await _host.CreateUserAsync(_seed.Tenant.Id, "tenant-admin", isSystemAdmin: false, administrators.Id);

        using IServiceScope scope = _host.Scope(c => c.RunAs(tenantAdmin.Id, tenantAdmin.TenantId, false, Permissions.All, tenantAdmin.DisplayName));
        var admins = scope.ServiceProvider.GetRequiredService<PlatformAdminService>();
        Assert.NotNull(await admins.GrantAsync("tenant-admin"));
        Assert.NotNull(await admins.RevokeAsync(_root.Id));
        Assert.Empty(await admins.ListAsync());
        Assert.False(await IsPlatformAdminAsync(tenantAdmin.Id));
    }

    [DbFact]
    public async Task A_new_tenant_gets_its_first_administrator_and_nothing_is_left_when_that_fails()
    {
        using (IServiceScope scope = As(_root))
        {
            var tenants = scope.ServiceProvider.GetRequiredService<TenantService>();
            (Tenant? created, string? error) = await tenants.CreateAsync("Customer", null, firstAdministrator: new FirstAdministrator("boss", "The Boss", "Boss-Passw0rd!"));
            Assert.Null(error);

            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            User boss = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.LoginName == "boss");
            Assert.Equal(created!.Id, boss.TenantId);
            Assert.True(boss.MustChangePassword);
            Assert.False(boss.IsSystemAdmin);
            Assert.Equal(new[] { TenantService.AdministratorRoleName }, await db.UserRoles.IgnoreQueryFilters().Where(ur => ur.UserId == boss.Id).Select(ur => ur.Role!.Name).ToArrayAsync());

            // a login that is taken: no tenant either
            (Tenant? none, string? taken) = await tenants.CreateAsync("Second customer", null, firstAdministrator: new FirstAdministrator("alice", "Alice again", "Boss-Passw0rd!"));
            Assert.Null(none);
            Assert.NotNull(taken);
            Assert.False(await db.Tenants.AnyAsync(t => t.Name == "Second customer"));
        }
    }
}

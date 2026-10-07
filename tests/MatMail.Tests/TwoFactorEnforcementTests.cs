using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>Who has to use two-factor authentication: the policy of the tenant and the flag of a role.</summary>
public class TwoFactorEnforcementTests : IAsyncLifetime
{
    private readonly TestClock _clock = new();
    private TestHost _host = null!;
    private Seed _seed = null!;
    private User _manager = null!;
    private User _root = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddSingleton<TimeProvider>(_clock));
        _seed = await _host.SeedAsync();

        Role managers = await _host.CreateRoleAsync(_seed.Tenant.Id, "Mailbox managers", requiresTwoFactor: false, Permissions.MailUse, Permissions.MailboxesManage);
        _manager = await _host.CreateUserAsync(_seed.Tenant.Id, "manager", isSystemAdmin: false, managers.Id);
        _root = await _host.CreateUserAsync(_seed.Tenant.Id, "root", isSystemAdmin: true);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<TwoFactorStatus> StatusAsync(User user)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<TwoFactorService>().GetStatusAsync(user.Id);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The policy of the tenant
    // -------------------------------------------------------------------------------------------------------------------

    [DbTheory]
    [InlineData(TwoFactorMode.Optional, false, false, false)]
    [InlineData(TwoFactorMode.Administrators, false, true, true)]
    [InlineData(TwoFactorMode.Everyone, true, true, true)]
    public async Task The_policy_binds_the_users_it_names(TwoFactorMode mode, bool plainUser, bool anyAdministrativePermission, bool systemAdministrator)
    {
        await _host.SetPolicyAsync(_seed.Tenant.Id, mode);

        Assert.Equal(plainUser, (await StatusAsync(_seed.Alice)).Required);
        Assert.Equal(anyAdministrativePermission, (await StatusAsync(_manager)).Required);
        Assert.Equal(systemAdministrator, (await StatusAsync(_root)).Required);
    }

    [DbFact]
    public async Task Using_mail_is_not_administering()
    {
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Administrators);

        // Alice holds mail.use and nothing else; the Unassigned permission is a mail permission, but still "another permission".
        Assert.False((await StatusAsync(_seed.Alice)).Required);
        Role helpers = await _host.CreateRoleAsync(_seed.Tenant.Id, "Unassigned helpers", requiresTwoFactor: false, Permissions.MailUse, Permissions.UnassignedManage);
        User helper = await _host.CreateUserAsync(_seed.Tenant.Id, "helper", isSystemAdmin: false, helpers.Id);
        Assert.True((await StatusAsync(helper)).Required);
    }

    [Fact]
    public void The_rule_in_a_nutshell()
    {
        string[] mailOnly = { Permissions.MailUse };
        string[] more = { Permissions.MailUse, Permissions.LogsView };
        string[] unknown = { Permissions.MailUse, "retired.permission" };

        Assert.False(TwoFactorPolicy.IsRequired(TwoFactorMode.Optional, false, more, false));
        Assert.True(TwoFactorPolicy.IsRequired(TwoFactorMode.Optional, false, mailOnly, anyRoleRequires: true));
        Assert.False(TwoFactorPolicy.IsRequired(TwoFactorMode.Administrators, false, mailOnly, false));
        Assert.False(TwoFactorPolicy.IsRequired(TwoFactorMode.Administrators, false, unknown, false));
        Assert.True(TwoFactorPolicy.IsRequired(TwoFactorMode.Administrators, false, more, false));
        Assert.True(TwoFactorPolicy.IsRequired(TwoFactorMode.Administrators, true, Array.Empty<string>(), false));
        Assert.True(TwoFactorPolicy.IsRequired(TwoFactorMode.Everyone, false, Array.Empty<string>(), false));
    }

    [DbFact]
    public async Task Only_someone_who_manages_security_changes_the_policy_and_it_is_logged()
    {
        using (IServiceScope plain = _host.ScopeAs(_seed.Alice, Permissions.MailUse, Permissions.UsersManage))
        {
            Assert.Equal("You are not allowed to do this.", await plain.ServiceProvider.GetRequiredService<TwoFactorPolicy>().SetModeAsync(_seed.Tenant.Id, TwoFactorMode.Everyone));
        }

        Assert.Equal(TwoFactorMode.Optional, await ModeAsync());

        using (IServiceScope admin = _host.ScopeAs(_manager, Permissions.SecurityManage))
        {
            Assert.Null(await admin.ServiceProvider.GetRequiredService<TwoFactorPolicy>().SetModeAsync(_seed.Tenant.Id, TwoFactorMode.Everyone));
        }

        Assert.Equal(TwoFactorMode.Everyone, await ModeAsync());
        ActivityLog entry = await _host.ReadAsync(db => db.ActivityLogs.AsNoTracking().SingleAsync(l => l.Message.Contains("two-factor policy")));
        Assert.Equal(ActivityCategory.Admin, entry.Category);
        Assert.Contains("Everyone", entry.Message);
    }

    private Task<TwoFactorMode> ModeAsync()
        => _host.ReadAsync(db => db.Tenants.AsNoTracking().Where(t => t.Id == _seed.Tenant.Id).Select(t => t.TwoFactorMode).SingleAsync());

    [DbFact]
    public async Task Every_new_tenant_gets_an_administrator_role_that_can_manage_security()
    {
        using IServiceScope scope = _host.Scope();
        (Tenant? tenant, _) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Fresh", null);

        string[] permissions = await _host.ReadAsync(db => db.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenant!.Id && r.Name == TenantService.AdministratorRoleName).Select(r => r.Permissions).SingleAsync());

        Assert.Contains(Permissions.SecurityManage, permissions);
        Assert.Equal(TwoFactorMode.Optional, await _host.ReadAsync(db => db.Tenants.Where(t => t.Id == tenant!.Id).Select(t => t.TwoFactorMode).SingleAsync()));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The flag of a role
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_role_can_require_it_from_its_members_only()
    {
        Role strict = await _host.CreateRoleAsync(_seed.Tenant.Id, "Finance", requiresTwoFactor: true, Permissions.MailUse);
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _seed.Bob.Id, RoleId = strict.Id });
            await db.SaveChangesAsync();
        }

        Assert.True((await StatusAsync(_seed.Bob)).Required);
        Assert.False((await StatusAsync(_seed.Alice)).Required);
        Assert.False((await StatusAsync(_manager)).Required);
    }

    [DbFact]
    public async Task A_user_who_has_set_it_up_is_not_asked_to_again()
    {
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Everyone);
        TwoFactorStatus before = await StatusAsync(_seed.Alice);
        Assert.True(before.SetupRequired);

        await _host.EnrolAsync(_clock, _seed.Alice);

        TwoFactorStatus after = await StatusAsync(_seed.Alice);
        Assert.True(after.Required);
        Assert.True(after.Enabled);
        Assert.False(after.SetupRequired);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // What the session sees
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_session_carries_the_state_and_follows_the_policy_at_once()
    {
        Guid token = await OpenSessionAsync(_seed.Alice, _seed.Tenant.Id);

        SessionSnapshot before = (await SnapshotAsync(token))!;
        Assert.False(before.TwoFactorRequired);
        Assert.False(before.TwoFactorEnabled);

        // The cache holds the snapshot for a few seconds; changing the policy empties it.
        using (IServiceScope cacheScope = _host.Scope())
        {
            cacheScope.ServiceProvider.GetRequiredService<SessionCache>().Set(token, before);
        }

        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Everyone);
        using (IServiceScope cacheScope = _host.Scope())
        {
            Assert.False(cacheScope.ServiceProvider.GetRequiredService<SessionCache>().TryGet(token, out _));
        }

        SessionSnapshot bound = (await SnapshotAsync(token))!;
        Assert.True(bound.TwoFactorRequired);
        Assert.False(bound.TwoFactorEnabled);
        ClaimsPrincipal principal = SignInService.BuildPrincipal(bound, token);
        Assert.Equal("1", principal.FindFirstValue(AppClaims.TwoFactorRequired));
        Assert.Equal("0", principal.FindFirstValue(AppClaims.TwoFactor));

        await _host.EnrolAsync(_clock, _seed.Alice, keepSession: token);
        SessionSnapshot done = (await SnapshotAsync(token))!;
        Assert.True(done.TwoFactorEnabled);
        Assert.Equal("1", SignInService.BuildPrincipal(done, token).FindFirstValue(AppClaims.TwoFactor));
    }

    [DbFact]
    public async Task A_system_administrator_follows_the_policy_of_the_tenant_they_belong_to()
    {
        Tenant other;
        using (IServiceScope scope = _host.Scope())
        {
            (Tenant? created, _) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Customer", null);
            other = created!;
        }

        await _host.SetPolicyAsync(other.Id, TwoFactorMode.Everyone);

        // root belongs to "Home" (optional) and works in "Customer" (everyone) ...
        Guid token = await OpenSessionAsync(_root, other.Id);
        SessionSnapshot working = (await SnapshotAsync(token))!;
        Assert.Equal(other.Id, working.TenantId);
        Assert.False(working.TwoFactorRequired);

        // ... and the other way round: the policy of "Home" counts, wherever root works.
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Administrators);
        Assert.True((await SnapshotAsync(token))!.TwoFactorRequired);
        await _host.SetPolicyAsync(other.Id, TwoFactorMode.Optional);
        Assert.True((await SnapshotAsync(token))!.TwoFactorRequired);
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Optional);
        Assert.False((await SnapshotAsync(token))!.TwoFactorRequired);
    }

    [DbFact]
    public async Task The_security_page_counts_who_still_has_to_set_it_up()
    {
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Everyone);
        await _host.EnrolAsync(_clock, _seed.Alice);

        using IServiceScope scope = _host.Scope();
        TwoFactorSummary summary = await scope.ServiceProvider.GetRequiredService<TwoFactorPolicy>().GetSummaryAsync(_seed.Tenant.Id);

        // alice, bob, manager and root: one of them has it, the other three have to.
        Assert.Equal(4, summary.Users);
        Assert.Equal(1, summary.Enabled);
        Assert.Equal(3, summary.StillToSetUp);
    }

    private async Task<Guid> OpenSessionAsync(User user, long tenantId)
    {
        var token = Guid.NewGuid();
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.UserSessions.Add(new UserSession { Token = token, UserId = user.Id, TenantId = tenantId, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<SessionSnapshot?> SnapshotAsync(Guid token)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().LoadSnapshotAsync(token, touch: false);
    }
}

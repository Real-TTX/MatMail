using System.Reflection;
using System.Text.RegularExpressions;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>
/// People who work in several tenants (docs/concept-tenant-memberships.md): the tenant boundary is the most dangerous place of the
/// program, so these tests first try to cross it without permission. Tenant "Home" (seed) is A, tenant "Other" is B; carol lives in A.
/// </summary>
public class TenantMembershipTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private Tenant _b = null!;
    private User _adminA = null!;
    private User _adminB = null!;
    private User _carol = null!;
    private User _dave = null!;
    private long _a;
    private Role _adminRoleA = null!;
    private Role _adminRoleB = null!;
    private Role _userRoleB = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _a = _seed.Tenant.Id;

        using (IServiceScope scope = _host.Scope())
        {
            (Tenant? tenant, string? error) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Other", null);
            Assert.Null(error);
            _b = tenant!;
        }

        _adminRoleA = await RoleAsync(_a, TenantService.AdministratorRoleName);
        _adminRoleB = await RoleAsync(_b.Id, TenantService.AdministratorRoleName);
        _userRoleB = await RoleAsync(_b.Id, TenantService.UserRoleName);
        long userRoleA = (await RoleAsync(_a, TenantService.UserRoleName)).Id;

        _adminA = await _host.CreateUserAsync(_a, "admin-a", isSystemAdmin: false, _adminRoleA.Id);
        _adminB = await _host.CreateUserAsync(_b.Id, "admin-b", isSystemAdmin: false, _adminRoleB.Id);
        _carol = await CreateWithMailboxAsync("carol", userRoleA);
        _dave = await CreateWithMailboxAsync("dave", userRoleA);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // -------------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------------

    private async Task<Role> RoleAsync(long tenantId, string name)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Roles.AsNoTracking().FirstAsync(r => r.TenantId == tenantId && r.Name == name);
    }

    private async Task<User> CreateWithMailboxAsync(string login, long roleId)
    {
        using IServiceScope scope = _host.Scope();
        (User? user, string? error) = await scope.ServiceProvider.GetRequiredService<UserService>().CreateAsync(
            new UserInput { LoginName = login, DisplayName = login, Password = TwoFactorTestExtensions.Password, RoleIds = new[] { roleId }, CreateMailbox = true, PrimaryAddress = $"{login}@{TestHost.Domain}" },
            _a);
        Assert.Null(error);
        return user!;
    }

    /// <summary>A web session of the person in the given tenant, with the permissions their roles there give.</summary>
    private IServiceScope As(User user, long tenantId, params string[] permissions)
        => _host.Scope(c => c.RunAs(user.Id, tenantId, user.IsSystemAdmin, permissions, user.DisplayName));

    private IServiceScope AdminOfB() => As(_adminB, _b.Id, Permissions.All.ToArray());

    private async Task<TenantInvitation> InviteAsync(string identifier, params long[] roleIds)
    {
        using IServiceScope scope = AdminOfB();
        (TenantInvitation? invitation, string? error) = await scope.ServiceProvider.GetRequiredService<MembershipService>().InviteAsync(identifier, roleIds);
        Assert.Null(error);
        return invitation!;
    }

    private async Task<string?> AcceptAsync(User user, long invitationId)
    {
        using IServiceScope scope = As(user, user.TenantId);
        return await scope.ServiceProvider.GetRequiredService<MembershipService>().AcceptAsync(invitationId);
    }

    private async Task JoinAsync(User user, params long[] roleIds)
    {
        TenantInvitation invitation = await InviteAsync(user.LoginName, roleIds);
        Assert.Null(await AcceptAsync(user, invitation.Id));
    }

    /// <summary>A session row of the person asking for the tenant, and what the application makes of it.</summary>
    private async Task<(Guid Token, SessionSnapshot? Snapshot)> SessionAsync(User user, long tenantId)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var session = new UserSession { Token = Guid.NewGuid(), UserId = user.Id, TenantId = tenantId, ExpiresDate = DateTime.UtcNow.AddHours(1), LastSeenDate = DateTime.UtcNow };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return (session.Token, await scope.ServiceProvider.GetRequiredService<SignInService>().LoadSnapshotAsync(session.Token, touch: false));
    }

    private async Task<SessionSnapshot?> SnapshotAsync(Guid token)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().LoadSnapshotAsync(token, touch: false);
    }

    private async Task<bool> SwitchAsync(Guid token, long tenantId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().SwitchTenantAsync(token, tenantId);
    }

    private async Task<T> DbAsync<T>(Func<MatMailDbContext, Task<T>> query)
    {
        using IServiceScope scope = _host.Scope();
        return await query(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    private Task ChangeAsync(Func<MatMailDbContext, Task> change) => DbAsync(async db => { await change(db); return 0; });

    // -------------------------------------------------------------------------------------------------------------------
    // Crossing the boundary without permission
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Nobody_switches_into_a_tenant_without_a_membership()
    {
        (Guid token, SessionSnapshot? home) = await SessionAsync(_carol, _a);
        Assert.Equal(_a, home!.TenantId);

        Assert.False(await SwitchAsync(token, _b.Id));                          // nothing at all
        TenantInvitation invitation = await InviteAsync("carol");
        Assert.False(await SwitchAsync(token, _b.Id));                          // invited only
        using (IServiceScope scope = As(_carol, _a))
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MembershipService>().DeclineAsync(invitation.Id));
        }

        Assert.False(await SwitchAsync(token, _b.Id));                          // declined
        TenantInvitation second = await InviteAsync("carol");
        using (IServiceScope scope = AdminOfB())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MembershipService>().RevokeAsync(second.Id));
        }

        Assert.False(await SwitchAsync(token, _b.Id));                          // revoked
        Assert.False(await SwitchAsync(token, 999_999));                        // a tenant that is not there
        Assert.Equal(_a, (await SnapshotAsync(token))!.TenantId);
    }

    [DbFact]
    public async Task A_session_that_asks_for_a_tenant_it_is_not_in_works_in_the_home_tenant()
    {
        // somebody wrote another tenant into the session row (nothing in the program does that for a plain user)
        (_, SessionSnapshot? snapshot) = await SessionAsync(_carol, _b.Id);

        Assert.Equal(_a, snapshot!.TenantId);
        Assert.DoesNotContain(Permissions.UsersManage, snapshot.Permissions);
    }

    [DbFact]
    public async Task The_permissions_of_a_session_are_those_of_the_roles_of_the_active_tenant()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _carol.Id, RoleId = _adminRoleA.Id });   // administrator at home
            await db.SaveChangesAsync();
        }

        await JoinAsync(_carol, _userRoleB.Id);                                                // plain user in B
        (Guid token, SessionSnapshot? home) = await SessionAsync(_carol, _a);
        Assert.Contains(Permissions.UsersManage, home!.Permissions);

        Assert.True(await SwitchAsync(token, _b.Id));
        SessionSnapshot inB = (await SnapshotAsync(token))!;
        Assert.Equal(_b.Id, inB.TenantId);
        Assert.Equal(_a, inB.HomeTenantId);
        Assert.Equal(new[] { Permissions.MailUse }, inB.Permissions.Distinct().OrderBy(p => p));

        // and the other way round: administrator in B does not make an administrator at home
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == _carol.Id && ur.RoleId == _adminRoleA.Id));
            db.UserRoles.Add(new UserRole { UserId = _carol.Id, RoleId = _adminRoleB.Id });
            await db.SaveChangesAsync();
        }

        SessionSnapshot inBAgain = (await SnapshotAsync(token))!;
        Assert.Contains(Permissions.UsersManage, inBAgain.Permissions);
        Assert.True(await SwitchAsync(token, _a));
        Assert.DoesNotContain(Permissions.UsersManage, (await SnapshotAsync(token))!.Permissions);
    }

    [DbFact]
    public async Task A_role_of_a_tenant_the_person_does_not_belong_to_is_not_assigned()
    {
        await JoinAsync(_carol);

        using IServiceScope scope = AdminOfB();
        var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();
        Assert.Null(await memberships.SetMemberRolesAsync(_carol.Id, new[] { _adminRoleA.Id, _userRoleB.Id }));   // a role of A smuggled in

        long[] held = await DbAsync(db => db.UserRoles.Where(ur => ur.UserId == _carol.Id).Select(ur => ur.RoleId).ToArrayAsync());
        Assert.DoesNotContain(_adminRoleA.Id, held);
        Assert.Contains(_userRoleB.Id, held);

        // nor for somebody who is no member of B at all
        Assert.NotNull(await memberships.SetMemberRolesAsync(_dave.Id, new[] { _userRoleB.Id }));
        Assert.Empty(await DbAsync(db => db.UserRoles.Where(ur => ur.UserId == _dave.Id && ur.RoleId == _userRoleB.Id).ToListAsync()));
    }

    [DbFact]
    public async Task Nobody_hands_out_a_role_with_permissions_they_do_not_hold()
    {
        Role inviters = await _host.CreateRoleAsync(_b.Id, "Inviters", requiresTwoFactor: false, Permissions.MailUse, Permissions.UsersManage);
        User inviter = await _host.CreateUserAsync(_b.Id, "inviter", isSystemAdmin: false, inviters.Id);

        using IServiceScope scope = As(inviter, _b.Id, Permissions.MailUse, Permissions.UsersManage);
        var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();

        (TenantInvitation? refused, string? error) = await memberships.InviteAsync("carol", new[] { _adminRoleB.Id });
        Assert.Null(refused);
        Assert.NotNull(error);
        (TenantInvitation? allowed, string? fine) = await memberships.InviteAsync("carol", new[] { _userRoleB.Id });
        Assert.Null(fine);
        Assert.Equal(new[] { _userRoleB.Id }, allowed!.RoleIds);

        // somebody without the permission to manage people cannot invite at all
        using IServiceScope plain = As(_carol, _a, Permissions.MailUse);
        Assert.NotNull((await plain.ServiceProvider.GetRequiredService<MembershipService>().InviteAsync("dave", Array.Empty<long>())).Error);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Invitations
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_person_who_accepts_works_in_the_other_tenant_and_switches_back_and_forth()
    {
        TenantInvitation invitation = await InviteAsync("carol", _userRoleB.Id);
        using (IServiceScope scope = As(_carol, _a))
        {
            var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();
            InvitationOffer offer = Assert.Single(await memberships.ListOffersAsync());
            Assert.Equal(("Other", "admin-b"), (offer.TenantName, offer.InvitedBy));
            Assert.Equal(new[] { TenantService.UserRoleName }, offer.Roles);
            Assert.Equal(new[] { "Home" }, (await memberships.ListTenantsAsync()).Select(t => t.Name));
        }

        Assert.Null(await AcceptAsync(_carol, invitation.Id));

        using (IServiceScope scope = As(_carol, _a))
        {
            var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();
            Assert.Equal(new[] { ("Home", true), ("Other", false) }, (await memberships.ListTenantsAsync()).Select(t => (t.Name, t.IsHome)));
            Assert.Empty(await memberships.ListOffersAsync());
        }

        (Guid token, SessionSnapshot? home) = await SessionAsync(_carol, _a);
        Assert.Equal("Home", home!.TenantName);
        Assert.True(await SwitchAsync(token, _b.Id));
        SessionSnapshot inB = (await SnapshotAsync(token))!;
        Assert.Equal(("Other", _b.Id), (inB.TenantName, inB.TenantId));
        Assert.True(await SwitchAsync(token, _a));
        Assert.Equal(_a, (await SnapshotAsync(token))!.TenantId);

        // the entry shows in the activity log of the tenant that was entered
        Assert.Contains(await DbAsync(db => db.ActivityLogs.Where(l => l.TenantId == _b.Id).Select(l => l.Message).ToListAsync()), m => m.Contains("switched to the tenant"));
    }

    [DbFact]
    public async Task An_invitation_does_not_tell_whether_the_person_exists()
    {
        TenantInvitation ghost = await InviteAsync("nobody-has-this-login");
        TenantInvitation carol = await InviteAsync("  CAROL ");

        Assert.Equal(InvitationState.Open, ghost.State);
        Assert.Equal(InvitationState.Open, carol.State);
        using IServiceScope scope = As(_carol, _a);
        Assert.Equal(carol.Id, Assert.Single(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListOffersAsync()).Id);

        // but whoever already belongs to the tenant is told so
        using IServiceScope admin = AdminOfB();
        Assert.Equal("That person already belongs to this tenant.", (await admin.ServiceProvider.GetRequiredService<MembershipService>().InviteAsync("admin-b", Array.Empty<long>())).Error);
    }

    [DbFact]
    public async Task An_invitation_is_for_the_login_or_an_address_of_an_owned_mailbox_and_never_for_the_contact_address_of_the_profile()
    {
        // dave types somebody else's address into his own profile
        await ChangeAsync(async db => await db.Users.Where(u => u.Id == _dave.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, $"carol@{TestHost.Domain}")));

        TenantInvitation byAddress = await InviteAsync($"carol@{TestHost.Domain}");

        using (IServiceScope scope = As(_dave, _a))
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListOffersAsync());
        }

        Assert.NotNull(await AcceptAsync(_dave, byAddress.Id));
        using IServiceScope carol = As(_carol, _a);
        Assert.Equal(byAddress.Id, Assert.Single(await carol.ServiceProvider.GetRequiredService<MembershipService>().ListOffersAsync()).Id);
        Assert.Null(await AcceptAsync(_carol, byAddress.Id));
    }

    [DbFact]
    public async Task Somebody_else_can_neither_accept_nor_decline_an_invitation_and_gets_the_same_answer_as_for_none()
    {
        TenantInvitation invitation = await InviteAsync("carol");

        string? accept = await AcceptAsync(_dave, invitation.Id);
        string? missing = await AcceptAsync(_dave, 999_999);
        using (IServiceScope scope = As(_dave, _a))
        {
            Assert.Equal(accept, await scope.ServiceProvider.GetRequiredService<MembershipService>().DeclineAsync(invitation.Id));
        }

        Assert.NotNull(accept);
        Assert.Equal(missing, accept);
        Assert.Equal(InvitationState.Open, (await DbAsync(db => db.TenantInvitations.IgnoreQueryFilters().FirstAsync(i => i.Id == invitation.Id))).State);
        Assert.Null(await AcceptAsync(_carol, invitation.Id));
    }

    [DbFact]
    public async Task An_invitation_works_once_and_not_after_it_expired_was_revoked_or_the_tenant_was_switched_off()
    {
        TenantInvitation expired = await InviteAsync("carol");
        await ChangeAsync(async db => await db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == expired.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresDate, DateTime.UtcNow.AddMinutes(-1))));
        Assert.NotNull(await AcceptAsync(_carol, expired.Id));

        TenantInvitation revoked = await InviteAsync("carol");
        using (IServiceScope scope = AdminOfB())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MembershipService>().RevokeAsync(revoked.Id));
        }

        Assert.NotNull(await AcceptAsync(_carol, revoked.Id));

        TenantInvitation off = await InviteAsync("carol");
        await ChangeAsync(async db => await db.Tenants.Where(t => t.Id == _b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false)));
        Assert.NotNull(await AcceptAsync(_carol, off.Id));
        await ChangeAsync(async db => await db.Tenants.Where(t => t.Id == _b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, true)));

        TenantInvitation once = await InviteAsync("carol");
        Assert.Null(await AcceptAsync(_carol, once.Id));
        Assert.NotNull(await AcceptAsync(_carol, once.Id));
        Assert.Equal(1, await DbAsync(db => db.TenantMemberships.IgnoreQueryFilters().CountAsync(m => m.UserId == _carol.Id)));
    }

    [DbFact]
    public async Task An_invitation_is_limited_and_inviting_again_replaces_the_open_one()
    {
        TenantInvitation first = await InviteAsync("carol", _userRoleB.Id);
        TenantInvitation again = await InviteAsync("carol", _adminRoleB.Id);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(new[] { _adminRoleB.Id }, (await DbAsync(db => db.TenantInvitations.IgnoreQueryFilters().SingleAsync(i => i.TenantId == _b.Id))).RoleIds);

        using IServiceScope scope = AdminOfB();
        var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();
        for (int i = 0; i < MembershipService.MaxInvitationsPerHour - 1; i++)
        {
            Assert.Null((await memberships.InviteAsync($"person{i}", Array.Empty<long>())).Error);
        }

        Assert.NotNull((await memberships.InviteAsync("one-too-many", Array.Empty<long>())).Error);
        Assert.NotNull((await memberships.InviteAsync("has space", Array.Empty<long>())).Error);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The end of a membership
    // -------------------------------------------------------------------------------------------------------------------

    private async Task<(Mailbox Shared, Mailbox Personal)> GiveCarolSomethingInBAsync()
    {
        using IServiceScope scope = _host.Scope();
        var mailboxes = scope.ServiceProvider.GetRequiredService<MailboxService>();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Mailbox shared = await mailboxes.CreateMailboxAsync("Shared of B", MailboxType.Shared, null, _b.Id);
        Mailbox personal = await mailboxes.CreateMailboxAsync("Carol in B", MailboxType.Personal, _carol.Id, _b.Id);
        db.MailboxPermissions.Add(new MailboxPermission { TenantId = _b.Id, MailboxId = shared.Id, UserId = _carol.Id, Access = MailboxAccess.Read });
        db.Signatures.Add(new Signature { TenantId = _b.Id, Scope = AppliesTo.User, UserId = _carol.Id, Name = "Carol in B", Html = "<p>Carol</p>" });
        db.MailTemplates.Add(new MailTemplate { TenantId = _b.Id, Scope = AppliesTo.User, UserId = _carol.Id, Name = "Carol's frame", Html = "{{Body}}" });
        await db.SaveChangesAsync();
        return (shared, personal);
    }

    [DbFact]
    public async Task Leaving_takes_the_roles_the_delegations_and_the_rows_of_that_tenant_with_it_and_leaves_the_home_tenant_alone()
    {
        await JoinAsync(_carol, _userRoleB.Id);
        (Mailbox shared, Mailbox personal) = await GiveCarolSomethingInBAsync();
        (Guid token, _) = await SessionAsync(_carol, _a);
        Assert.True(await SwitchAsync(token, _b.Id));

        using (IServiceScope scope = As(_carol, _b.Id, Permissions.MailUse))
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MembershipService>().LeaveAsync(_b.Id));
        }

        Assert.Empty(await DbAsync(db => db.TenantMemberships.IgnoreQueryFilters().Where(m => m.UserId == _carol.Id).ToListAsync()));
        Assert.Empty(await DbAsync(db => db.UserRoles.Where(ur => ur.UserId == _carol.Id && ur.RoleId == _userRoleB.Id).ToListAsync()));
        Assert.Empty(await DbAsync(db => db.MailboxPermissions.IgnoreQueryFilters().Where(p => p.UserId == _carol.Id && p.TenantId == _b.Id).ToListAsync()));
        Assert.Empty(await DbAsync(db => db.Signatures.IgnoreQueryFilters().Where(s => s.UserId == _carol.Id && s.TenantId == _b.Id).ToListAsync()));
        Assert.Empty(await DbAsync(db => db.MailTemplates.IgnoreQueryFilters().Where(t => t.UserId == _carol.Id && t.TenantId == _b.Id).ToListAsync()));
        Assert.Null((await DbAsync(db => db.Mailboxes.IgnoreQueryFilters().FirstAsync(m => m.Id == personal.Id))).OwnerUserId);   // the mailbox stays, without an owner
        Assert.True(await DbAsync(db => db.Mailboxes.IgnoreQueryFilters().AnyAsync(m => m.Id == shared.Id)));

        // at home nothing changed, and the open session is back at home instead of signed out
        Assert.Equal(_seed.Tenant.Id, (await DbAsync(db => db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == _carol.Id))).TenantId);
        Assert.Equal(1, await DbAsync(db => db.UserRoles.CountAsync(ur => ur.UserId == _carol.Id)));
        Assert.Equal(_carol.Id, (await DbAsync(db => db.Mailboxes.IgnoreQueryFilters().FirstAsync(m => m.TenantId == _a && m.OwnerUserId == _carol.Id))).OwnerUserId);
        Assert.Equal(_a, (await SnapshotAsync(token))!.TenantId);

        // an invitation later starts from nothing
        await JoinAsync(_carol);
        Assert.Empty(await DbAsync(db => db.MailboxPermissions.IgnoreQueryFilters().Where(p => p.UserId == _carol.Id && p.TenantId == _b.Id).ToListAsync()));
    }

    [DbFact]
    public async Task An_administrator_removes_a_member_and_decides_what_happens_to_their_mailbox()
    {
        await JoinAsync(_carol);
        (_, Mailbox personal) = await GiveCarolSomethingInBAsync();

        using IServiceScope scope = AdminOfB();
        var memberships = scope.ServiceProvider.GetRequiredService<MembershipService>();
        Assert.Null(await memberships.RemoveAsync(_carol.Id, deleteMailbox: true));

        Assert.False(await DbAsync(db => db.Mailboxes.IgnoreQueryFilters().AnyAsync(m => m.Id == personal.Id)));
        Assert.Empty(await memberships.ListMembersAsync());
        Assert.NotNull(await memberships.RemoveAsync(_carol.Id, deleteMailbox: false));          // not a member any more
        Assert.NotNull(await memberships.RemoveAsync(_dave.Id, deleteMailbox: false));           // never was

        // the home tenant cannot be left, and somebody of the home tenant cannot be "removed" by its administrators this way
        using IServiceScope atHome = As(_carol, _a, Permissions.MailUse);
        Assert.NotNull(await atHome.ServiceProvider.GetRequiredService<MembershipService>().LeaveAsync(_a));
        using IServiceScope adminA = As(_adminA, _a, Permissions.All.ToArray());
        Assert.NotNull(await adminA.ServiceProvider.GetRequiredService<MembershipService>().RemoveAsync(_dave.Id, deleteMailbox: false));
        Assert.True(await DbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == _dave.Id)));
    }

    [DbFact]
    public async Task The_session_goes_home_when_the_tenant_is_switched_off_and_when_the_member_is_removed()
    {
        await JoinAsync(_carol);
        (Guid token, _) = await SessionAsync(_carol, _a);
        Assert.True(await SwitchAsync(token, _b.Id));

        await ChangeAsync(async db => await db.Tenants.Where(t => t.Id == _b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false)));
        Assert.Equal(_a, (await SnapshotAsync(token))!.TenantId);
        Assert.False(await SwitchAsync(token, _b.Id));
        using (IServiceScope scope = As(_carol, _a))
        {
            Assert.Equal(new[] { "Home" }, (await scope.ServiceProvider.GetRequiredService<MembershipService>().ListTenantsAsync()).Select(t => t.Name));
        }

        await ChangeAsync(async db => await db.Tenants.Where(t => t.Id == _b.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, true)));
        Assert.True(await SwitchAsync(token, _b.Id));
        using (IServiceScope scope = AdminOfB())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MembershipService>().RemoveAsync(_carol.Id, deleteMailbox: false));
        }

        Assert.Equal(_a, (await SnapshotAsync(token))!.TenantId);
    }

    [DbFact]
    public async Task Deleting_the_person_or_the_tenant_leaves_nothing_behind()
    {
        await JoinAsync(_carol);
        await JoinAsync(_dave);
        await InviteAsync("somebody");

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<UserService>().DeleteAsync(_carol.Id, deleteMailbox: true));
        }

        Assert.Empty(await DbAsync(db => db.TenantMemberships.IgnoreQueryFilters().Where(m => m.UserId == _carol.Id).ToListAsync()));

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<TenantService>().DeleteAsync(_b.Id));
        }

        Assert.Empty(await DbAsync(db => db.TenantMemberships.IgnoreQueryFilters().ToListAsync()));
        Assert.Empty(await DbAsync(db => db.TenantInvitations.IgnoreQueryFilters().ToListAsync()));
        Assert.True(await DbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == _dave.Id)));       // the people stay, they only lose the tenant
    }

    [Fact]
    public void Everything_that_belongs_to_a_person_in_a_tenant_is_dealt_with_when_a_membership_ends()
    {
        // A new table of a tenant with a column for a person has to be added to MembershipService.EndMembershipAsync, or to this list with a reason.
        string[] dealtWith = { nameof(MailboxPermission), nameof(Signature), nameof(MailTemplate), nameof(Mailbox), nameof(TenantMembership) };
        string[] exempt = { nameof(OutboundMessage), nameof(TenantInvitation) };   // who sent a message, who answered an invitation: history, not access

        Regex personColumn = new("^(Owner|Sender|Recipient)?UserId$");
        string[] found = typeof(MatMailDbContext).Assembly.GetTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => personColumn.IsMatch(p.Name)))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(dealtWith.Concat(exempt).OrderBy(n => n), found);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // What the administrators of a tenant see
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_mailbox_belongs_only_to_somebody_of_its_tenant()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var mailboxes = scope.ServiceProvider.GetRequiredService<MailboxService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => mailboxes.CreateMailboxAsync("Dave in B", MailboxType.Personal, _dave.Id, _b.Id));
            Assert.NotNull(await mailboxes.CreateMailboxAsync("Admin of B", MailboxType.Personal, _adminB.Id, _b.Id));       // at home
        }

        await JoinAsync(_dave);
        using (IServiceScope scope = _host.Scope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<MailboxService>().CreateMailboxAsync("Dave in B", MailboxType.Personal, _dave.Id, _b.Id));   // as a member
        }
    }

    [DbFact]
    public async Task The_administrators_of_a_tenant_see_the_members_and_only_who_they_are()
    {
        await JoinAsync(_carol);

        using (IServiceScope scope = AdminOfB())
        {
            TenantMember member = Assert.Single(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListMembersAsync());
            Assert.Equal(("carol", "carol"), (member.LoginName, member.DisplayName));
        }

        // the home tenant sees no members of B, and somebody without the permission sees nothing at all
        using (IServiceScope scope = As(_adminA, _a, Permissions.All.ToArray()))
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListMembersAsync());
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListInvitationsAsync());
        }

        using (IServiceScope scope = As(_adminB, _b.Id, Permissions.MailUse))
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MembershipService>().ListMembersAsync());
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<MembershipService>().RevokeAsync(1));
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Mail programs and the second factor
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Mail_programs_work_in_the_home_tenant_with_the_roles_held_there()
    {
        await JoinAsync(_carol, _adminRoleB.Id);                  // administrator in B, plain user at home

        using IServiceScope scope = _host.Scope();
        MailUser? user = await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync("carol", TwoFactorTestExtensions.Password, "127.0.0.1");

        Assert.NotNull(user);
        Assert.Equal(_a, user.TenantId);
        Assert.True(user.Can(Permissions.MailUse));
        Assert.False(user.Can(Permissions.UsersManage));
    }

    [DbFact]
    public async Task The_second_factor_of_the_home_tenant_binds_everywhere_and_that_of_another_tenant_only_while_the_member_works_there()
    {
        await JoinAsync(_carol);
        (Guid token, SessionSnapshot? home) = await SessionAsync(_carol, _a);
        Assert.False(home!.TwoFactorRequired);

        await _host.SetPolicyAsync(_b.Id, TwoFactorMode.Everyone);
        Assert.False((await SnapshotAsync(token))!.TwoFactorRequired);
        Assert.True(await SwitchAsync(token, _b.Id));
        Assert.True((await SnapshotAsync(token))!.TwoFactorRequired);          // held on the security page in B ...
        Assert.True(await SwitchAsync(token, _a));
        Assert.False((await SnapshotAsync(token))!.TwoFactorRequired);         // ... and not at home
        using (IServiceScope scope = _host.Scope())
        {
            Assert.False((await scope.ServiceProvider.GetRequiredService<TwoFactorService>().GetStatusAsync(_carol.Id)).Required);   // nor in the mail programs
        }

        await _host.SetPolicyAsync(_b.Id, TwoFactorMode.Optional);
        await _host.SetPolicyAsync(_a, TwoFactorMode.Everyone);                // the home tenant binds her wherever she works
        Assert.True(await SwitchAsync(token, _b.Id));
        Assert.True((await SnapshotAsync(token))!.TwoFactorRequired);
        using (IServiceScope scope = _host.Scope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TwoFactorService>().GetStatusAsync(_carol.Id)).Required);
        }
    }
}

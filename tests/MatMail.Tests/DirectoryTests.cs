using MatMail.Data;
using MatMail.Directories;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests;

public class LdapFilterTests
{
    [Theory]
    [InlineData("fry", "fry")]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("(uid=*)", "\\28uid=\\2a\\29")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("nul\0", "nul\\00")]
    [InlineData("Müller", "M\\c3\\bcller")]
    [InlineData("fry)(uid=*", "fry\\29\\28uid=\\2a")]
    public void What_a_person_types_cannot_change_the_filter_around_it(string value, string expected)
        => Assert.Equal(expected, LdapFilter.Escape(value));

    [Fact]
    public void Filters_are_joined_and_put_in_brackets()
    {
        Assert.Equal("(a=1)", LdapFilter.And("a=1"));
        Assert.Equal("(&(a=1)(b=2))", LdapFilter.And("(a=1)", "b=2", null, "  "));
        Assert.Equal("(objectClass=*)", LdapFilter.And(null, ""));
    }

    [Theory]
    [InlineData("(a=1)", true)]
    [InlineData("(&(a=1)(|(b=2)(c=3)))", true)]
    [InlineData("(a=1", false)]
    [InlineData("a=1)", false)]
    [InlineData("(a=\\29)", true)]
    [InlineData("a=1", false)]
    [InlineData("", false)]
    public void A_filter_is_checked_for_its_brackets(string filter, bool balanced) => Assert.Equal(balanced, LdapFilter.IsBalanced(filter));

    [Theory]
    [InlineData("CN=Fry,OU=People,DC=x", "cn=fry, ou=people ,dc=X", true)]
    [InlineData("CN=Fry,OU=People", "CN=Fry,OU=Other", false)]
    [InlineData(null, "CN=Fry", false)]
    public void Names_of_entries_compare_the_way_directories_do(string? first, string? second, bool same) => Assert.Equal(same, LdapFilter.SameDn(first, second));
}

public class DirectoryMappingTests
{
    private static DirectoryConnection Active() => new()
    {
        LoginAttribute = "sAMAccountName",
        DisplayNameAttribute = "displayName",
        EmailAttribute = "mail",
        FirstNameAttribute = "givenName",
        LastNameAttribute = "sn",
        JobTitleAttribute = "title",
        PhoneAttribute = "telephoneNumber",
        MobileAttribute = "mobile",
        DepartmentAttribute = "department",
    };

    [Fact]
    public void An_entry_becomes_a_person_through_the_mapper()
    {
        DirectoryEntry entry = DirectoryEntry.Of(
            "CN=Max Mustermann,OU=Staff,DC=firma,DC=test",
            ("sAMAccountName", ["MMustermann"]), ("displayName", ["Max Mustermann"]), ("mail", ["max@firma.test"]), ("givenName", ["Max"]), ("sn", ["Mustermann"]),
            ("title", ["Chef"]), ("telephoneNumber", ["+49 30 1234"]), ("department", ["Leitung"]), ("objectGUID", ["3f2c0a8e-0000-4000-8000-000000000001"]));

        DirectoryUser user = DirectoryService.MapUser(Active(), entry)!;

        Assert.Equal("mmustermann", user.Login);                       // logins are compared in lower case
        Assert.Equal("Max Mustermann", user.DisplayName);
        Assert.Equal("max@firma.test", user.Email);
        Assert.Equal("Chef", user.JobTitle);
        Assert.Equal("+49 30 1234", user.Phone);
        Assert.Null(user.Mobile);                                      // the entry has none
        Assert.Equal("3f2c0a8e-0000-4000-8000-000000000001", user.Uid);
        Assert.False(user.Disabled);
    }

    [Theory]
    [InlineData("512", false)]
    [InlineData("514", true)]     // normal account + disabled
    [InlineData("66050", true)]
    [InlineData("66048", false)]  // password never expires
    [InlineData("", false)]
    public void An_account_that_active_directory_has_disabled_is_told(string control, bool disabled)
    {
        DirectoryEntry entry = DirectoryEntry.Of("CN=X", ("sAMAccountName", ["x"]), ("userAccountControl", [control]));

        Assert.Equal(disabled, DirectoryService.MapUser(Active(), entry)!.Disabled);
    }

    [Fact]
    public void Without_a_login_an_entry_is_no_person_and_without_a_name_the_login_is_the_name()
    {
        Assert.Null(DirectoryService.MapUser(Active(), DirectoryEntry.Of("CN=No Login", ("displayName", ["No Login"]))));
        Assert.Equal("anna", DirectoryService.MapUser(Active(), DirectoryEntry.Of("CN=Anna", ("sAMAccountName", ["anna"])))!.DisplayName);
    }
}

/// <summary>People of a directory sign in with its passwords; what that makes of the users (against a directory in memory).</summary>
public class DirectoryProvisioningTests : IAsyncLifetime
{
    private readonly FakeDirectory _directory = new();
    private readonly TestClock _clock = new();
    private TestHost _host = null!;
    private Seed _seed = null!;
    private long _connectionId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: s =>
        {
            s.AddSingleton<IDirectoryClientFactory>(_directory);
            s.AddSingleton<TimeProvider>(_clock);
        });
        _seed = await _host.SeedAsync();
        await AddConnectionAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task AddConnectionAsync(Action<DirectoryConnection>? change = null)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var dir = new DirectoryConnection
        {
            TenantId = _seed.Tenant.Id,
            Name = "Office directory",
            Host = "ldap.example.test",
            Port = 389,
            BaseDn = FakeDirectory.People,
            UserFilter = "(objectClass=inetOrgPerson)",
            LoginAttribute = "uid",
            DisplayNameAttribute = "cn",
            EmailAttribute = "mail",
            FirstNameAttribute = "givenName",
            LastNameAttribute = "sn",
            PhoneAttribute = "telephoneNumber",
            JobTitleAttribute = null,
            MobileAttribute = null,
            DepartmentAttribute = null,
        };
        change?.Invoke(dir);
        db.DirectoryConnections.Add(dir);
        await db.SaveChangesAsync();
        _connectionId = dir.Id;
    }

    private async Task<SignInOutcome> SignInAsync(string login, string password, SignInPurpose purpose = SignInPurpose.Web, string ip = "192.0.2.5")
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().ValidateCredentialsAsync(login, password, ip, purpose);
    }

    private Task<User?> UserAsync(string login)
        => _host.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.LoginName == login));

    private Task UpdateConnectionAsync(Action<DirectoryConnection> change)
        => _host.WriteAsync(async db => change(await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync(d => d.Id == _connectionId)));

    // ---- the first sign-in makes the user ------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_person_of_the_directory_signs_in_the_first_time_and_has_a_user_afterwards()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test", phone: "+49 1");

        SignInOutcome outcome = await SignInAsync("FRY", "fry");

        Assert.True(outcome.Succeeded);
        User user = (await UserAsync("fry"))!;
        Assert.Equal(_seed.Tenant.Id, user.TenantId);
        Assert.Equal("Philip Fry", user.DisplayName);
        Assert.Equal("Philip", user.FirstName);
        Assert.Equal("fry@example.test", user.Email);
        Assert.Equal("+49 1", user.Phone);
        Assert.Equal(_connectionId, user.DirectoryId);
        Assert.Equal("cn=Philip Fry," + FakeDirectory.People, user.DirectoryDn);
        Assert.False(string.IsNullOrEmpty(user.DirectoryUid));

        // the plain user role of the tenant, a personal mailbox with the address of the directory (example.test is a domain of the tenant)
        Role role = (await _host.ReadAsync(db => db.Roles.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == user.UserRoles.Single().RoleId)));
        Assert.Equal(TenantService.UserRoleName, role.Name);
        Mailbox mailbox = await _host.ReadAsync(db => db.Mailboxes.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.OwnerUserId == user.Id));
        Assert.Equal(MailboxType.Personal, mailbox.Type);
        Assert.Equal("fry@example.test", await _host.ReadAsync(db => db.MailboxAliases.IgnoreQueryFilters().Where(a => a.MailboxId == mailbox.Id && a.IsPrimary).Select(a => a.Address).SingleAsync()));

        // the second time it is the same user, and the same password
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        Assert.Equal(1, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.LoginName == "fry")));
    }

    [DbFact]
    public async Task The_same_new_person_signing_in_many_times_at_once_gets_one_user_and_every_sign_in_works()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");

        SignInOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SignInAsync("fry", "fry")));

        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(1, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.LoginName == "fry")));
        Assert.Equal(1, await _host.ReadAsync(db => db.Mailboxes.IgnoreQueryFilters().CountAsync(m => m.Type == MailboxType.Personal && m.Name == "Philip Fry")));
    }

    [DbFact]
    public async Task A_wrong_password_makes_no_user_and_the_password_of_the_directory_is_the_only_one()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");

        Assert.False((await SignInAsync("fry", "not-it")).Succeeded);
        Assert.Null(await UserAsync("fry"));
        Assert.False((await SignInAsync("fry", "")).Succeeded);

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        // the user has a password hash of their own, which nobody knows: neither the empty password nor the one of the directory plus an a is taken
        Assert.False((await SignInAsync("fry", "fry-but-not")).Succeeded);

        // changed in the directory: the new one is good at once, the old one is not any more
        _directory.Passwords["cn=Philip Fry," + FakeDirectory.People] = "new-secret-1";
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);
        Assert.True((await SignInAsync("fry", "new-secret-1")).Succeeded);
    }

    [DbFact]
    public async Task Somebody_whom_the_group_does_not_let_in_is_not_made_a_user()
    {
        string crew = $"cn=ship_crew,{FakeDirectory.People}";
        await UpdateConnectionAsync(d => d.AllowedGroupDn = crew);
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test", groups: [crew]);
        _directory.AddPerson("professor", "Hubert Farnsworth", "professor@example.test");

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        Assert.False((await SignInAsync("professor", "professor")).Succeeded);
        Assert.Null(await UserAsync("professor"));
    }

    [DbFact]
    public async Task The_member_list_of_a_group_does_it_too_when_the_person_has_no_memberOf()
    {
        string crew = $"cn=ship_crew,{FakeDirectory.People}";
        await UpdateConnectionAsync(d => { d.AllowedGroupDn = crew; d.GroupLookup = GroupLookup.GroupMembers; });
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        _directory.AddPerson("professor", "Hubert Farnsworth", "professor@example.test");
        _directory.AddGroup("ship_crew", fry.Dn);

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        Assert.False((await SignInAsync("professor", "professor")).Succeeded);
    }

    [DbFact]
    public async Task An_account_that_is_disabled_in_the_directory_does_not_sign_in()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test", control: 514);

        Assert.False((await SignInAsync("fry", "fry")).Succeeded);
        Assert.Null(await UserAsync("fry"));
    }

    [DbFact]
    public async Task What_the_login_could_mean_to_a_filter_means_nothing()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");

        Assert.False((await SignInAsync("*", "fry")).Succeeded);
        Assert.False((await SignInAsync("fry)(uid=*", "fry")).Succeeded);
        Assert.False((await SignInAsync("f*", "fry")).Succeeded);
        Assert.Null(await UserAsync("fry"));
    }

    [DbFact]
    public async Task Without_the_permission_to_create_users_only_the_imported_ones_sign_in()
    {
        await UpdateConnectionAsync(d => d.CreateUsersOnSignIn = false);
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        _directory.AddPerson("leela", "Turanga Leela", "leela@example.test");

        using (IServiceScope scope = _host.Scope())
        {
            var provisioner = scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>();
            DirectoryConnection dir = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().DirectoryConnections.IgnoreQueryFilters().SingleAsync();
            (User? user, string? error) = await provisioner.CreateUserAsync(dir, DirectoryService.MapUser(dir, fry)!);
            Assert.Null(error);
            Assert.NotNull(user);
        }

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        Assert.False((await SignInAsync("leela", "leela")).Succeeded);
        Assert.Null(await UserAsync("leela"));
    }

    [DbFact]
    public async Task A_login_that_another_user_has_is_not_taken_over()
    {
        _directory.AddPerson("alice", "Alice Directory", "alice@example.test");

        // alice is a local user of the seed, with a password of her own
        Assert.True((await SignInAsync("alice", "Test-Passw0rd!")).Succeeded);
        Assert.False((await SignInAsync("alice", "alice")).Succeeded);

        User alice = (await UserAsync("alice"))!;
        Assert.Null(alice.DirectoryId);
        Assert.Equal(_seed.Alice.Id, alice.Id);
    }

    [DbFact]
    public async Task A_person_who_has_a_new_login_in_the_directory_is_still_the_same_user()
    {
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test", uuid: "11111111-2222-3333-4444-555555555555");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        long id = (await UserAsync("fry"))!.Id;

        // renamed there: another uid, the same entry (and a moved one: another distinguished name)
        DirectoryEntry renamed = DirectoryEntry.Of(
            $"cn=Philip Fry,ou=moved,{FakeDirectory.People}",
            ("objectClass", ["inetOrgPerson"]), ("uid", ["pfry"]), ("cn", ["Philip Fry"]), ("mail", ["fry@example.test"]), ("entryUUID", ["11111111-2222-3333-4444-555555555555"]));
        _directory.Replace(fry, renamed);

        Assert.True((await SignInAsync("pfry", "fry")).Succeeded);

        Assert.Equal(1, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.DirectoryId == _connectionId)));
        User user = (await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == id)));
        Assert.Equal("pfry", user.LoginName);
        Assert.Equal(renamed.Dn, user.DirectoryDn);
    }

    [DbFact]
    public async Task What_the_directory_says_is_taken_over_at_every_sign_in_but_what_it_does_not_say_stays()
    {
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test", phone: "+49 1");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        await _host.WriteAsync(async db => (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.LoginName == "fry")).Department = "Delivery");   // an administrator wrote it
        _directory.Replace(fry, DirectoryEntry.Of(fry.Dn, ("objectClass", ["inetOrgPerson"]), ("uid", ["fry"]), ("cn", ["Philip J. Fry"]), ("mail", ["fry@example.test"]), ("telephoneNumber", ["+49 2"])));

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        User user = (await UserAsync("fry"))!;
        Assert.Equal("Philip J. Fry", user.DisplayName);
        Assert.Equal("+49 2", user.Phone);
        Assert.Equal("Delivery", user.Department);   // the connection has no attribute for it
    }

    // ---- who may go on signing in ----------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_user_whom_the_directory_has_taken_out_cannot_sign_in_any_more_and_the_sessions_end()
    {
        string crew = $"cn=ship_crew,{FakeDirectory.People}";
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test", groups: [crew]);
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        long userId = (await UserAsync("fry"))!.Id;
        await _host.WriteAsync(db => { db.UserSessions.Add(new UserSession { Token = Guid.NewGuid(), UserId = userId, TenantId = _seed.Tenant.Id, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow }); return Task.CompletedTask; });

        // now only the crew is let in, and he is not in it any more
        await UpdateConnectionAsync(d => d.AllowedGroupDn = crew);
        _directory.Replace(fry, DirectoryEntry.Of(fry.Dn, ("objectClass", ["inetOrgPerson"]), ("uid", ["fry"]), ("cn", ["Philip Fry"]), ("entryUUID", [fry.First("entryUUID")!])));

        DirectorySyncResult result = await SyncAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Blocked);
        Assert.NotNull((await UserAsync("fry"))!.DirectoryDisabledDate);
        Assert.Equal(0, await _host.ReadAsync(db => db.UserSessions.CountAsync(s => s.UserId == userId)));
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);

        // back in the group: the next comparison lets him in again
        _directory.Replace(_directory.Entries.Single(e => e.Dn == fry.Dn), DirectoryEntry.Of(fry.Dn, ("objectClass", ["inetOrgPerson"]), ("uid", ["fry"]), ("cn", ["Philip Fry"]), ("memberOf", [crew]), ("entryUUID", [fry.First("entryUUID")!])));
        result = await SyncAsync();
        Assert.Equal(1, result.UnblockedAgain);
        Assert.Null((await UserAsync("fry"))!.DirectoryDisabledDate);
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
    }

    [DbFact]
    public async Task A_mail_program_that_is_signed_in_loses_its_session_when_the_directory_lets_the_person_go()
    {
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        MailUser? user;
        using (IServiceScope scope = _host.Scope())
        {
            user = await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync("fry", "fry", "192.0.2.5");
        }

        Assert.NotNull(user);   // IMAP and SMTP sessions stay open for hours: what the directory decides has to reach them
        using (IServiceScope scope = _host.Scope())
        {
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<MailAccessService>().RefreshAsync(user));
        }

        _directory.Remove(fry);
        await SyncAsync();

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MailAccessService>().RefreshAsync(user));
        }
    }

    [DbFact]
    public async Task A_person_who_is_gone_from_the_directory_is_blocked_but_not_deleted()
    {
        DirectoryEntry fry = _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        _directory.Remove(fry);
        DirectorySyncResult result = await SyncAsync();

        Assert.Equal(1, result.Blocked);
        Assert.NotNull(await UserAsync("fry"));
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);
    }

    [DbFact]
    public async Task A_directory_that_cannot_be_reached_changes_nothing_and_says_so()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        _directory.Down = true;
        DirectorySyncResult result = await SyncAsync();

        Assert.False(result.Ok);
        Assert.Contains("No connection", result.Message);
        Assert.Null((await UserAsync("fry"))!.DirectoryDisabledDate);
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);   // nobody can check the password
    }

    private async Task<DirectorySyncResult> SyncAsync()
    {
        using IServiceScope scope = _host.Scope();
        DirectoryConnection dir = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().DirectoryConnections.IgnoreQueryFilters().SingleAsync();
        return await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().SyncAsync(dir);
    }

    // ---- mail programs ------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Mail_programs_that_sign_in_again_and_again_are_not_a_question_to_the_directory_each_time()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        int binds = _directory.Binds;

        for (int i = 0; i < 5; i++)
        {
            Assert.True((await SignInAsync("fry", "fry", SignInPurpose.Protocol)).Succeeded);
        }

        Assert.Equal(binds + 1, _directory.Binds);                                          // the first IMAP sign-in asked, the others did not
        Assert.False((await SignInAsync("fry", "wrong", SignInPurpose.Protocol)).Succeeded);   // a password that was not the good one is never remembered
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);                           // the web always asks
        Assert.Equal(binds + 3, _directory.Binds);
    }

    [DbFact]
    public async Task Somebody_who_has_two_factor_authentication_signs_in_to_mail_programs_with_an_app_password_only()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        User fry = (await UserAsync("fry"))!;
        await _host.WriteAsync(db => { db.UserTotps.Add(new UserTotp { UserId = fry.Id, Secret = "secret", ConfirmedDate = DateTime.UtcNow }); return Task.CompletedTask; });

        SignInOutcome web = await SignInAsync("fry", "fry");
        SignInOutcome protocol = await SignInAsync("fry", "fry", SignInPurpose.Protocol);

        Assert.True(web.SecondFactorPending);     // the directory checks the password, the code is MatMail's
        Assert.False(protocol.Succeeded);
    }

    // ---- a local user switches to the directory ---------------------------------------------------------------------------------

    [DbFact]
    public async Task A_local_user_who_is_linked_to_the_directory_signs_in_with_its_password_and_not_with_the_old_one()
    {
        _directory.AddPerson("alice", "Alice Directory", "alice@example.test", password: "directory-secret");
        await _host.WriteAsync(db => { db.UserSessions.Add(new UserSession { Token = Guid.NewGuid(), UserId = _seed.Alice.Id, TenantId = _seed.Tenant.Id, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow }); return Task.CompletedTask; });
        Assert.True((await SignInAsync("alice", "Test-Passw0rd!")).Succeeded);

        string? error;
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            DirectoryConnection dir = await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync();
            DirectoryUser person = (await scope.ServiceProvider.GetRequiredService<DirectoryService>().FindUserAsync(dir, "alice")).User!;
            User alice = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _seed.Alice.Id);
            error = await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().LinkAsync(dir, person, alice);
        }

        Assert.Null(error);
        User linked = (await UserAsync("alice"))!;
        Assert.Equal(_connectionId, linked.DirectoryId);
        Assert.Equal("Alice Directory", linked.DisplayName);
        Assert.Equal(0, await _host.ReadAsync(db => db.UserSessions.CountAsync(s => s.UserId == _seed.Alice.Id)));   // the next sign-in is one of the new kind
        Assert.False((await SignInAsync("alice", "Test-Passw0rd!")).Succeeded);
        Assert.True((await SignInAsync("alice", "directory-secret")).Succeeded);
        Assert.Equal(1, await _host.ReadAsync(db => db.Mailboxes.IgnoreQueryFilters().CountAsync(m => m.OwnerUserId == _seed.Alice.Id)));   // the mailbox stays
    }

    [DbFact]
    public async Task Somebody_who_is_a_user_of_a_directory_already_is_not_linked_again()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        DirectoryConnection dir = await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync();
        User fry = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.LoginName == "fry");
        string? error = await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().LinkAsync(dir, DirectoryService.MapUser(dir, _directory.Entries.Single(e => e.First("uid") == "fry"))!, fry);

        Assert.Contains("already", error);
    }

    // ---- the pages of users ---------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Nobody_sets_a_password_of_a_user_the_directory_signs_in()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        User fry = (await UserAsync("fry"))!;

        using IServiceScope scope = _host.Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        string? changed = await users.ChangePasswordAsync(fry.Id, "A-Very-Long-Passw0rd");
        string? updated = await users.UpdateAsync(fry.Id, new UserInput { LoginName = "fry", DisplayName = "Philip Fry", Password = "A-Very-Long-Passw0rd", MustChangePassword = true });

        Assert.Equal(UserService.DirectoryPasswordMessage, changed);
        Assert.Equal(UserService.DirectoryPasswordMessage, updated);
        Assert.False((await SignInAsync("fry", "A-Very-Long-Passw0rd")).Succeeded);
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        // a save without a password is fine, and a user of a directory never has to choose a new password
        Assert.Null(await users.UpdateAsync(fry.Id, new UserInput { LoginName = "fry", DisplayName = "Philip J. Fry", MustChangePassword = true }));
        Assert.False((await UserAsync("fry"))!.MustChangePassword);
    }

    [DbFact]
    public async Task When_a_directory_is_deleted_its_people_stay_and_get_a_password_from_an_administrator()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        await _host.WriteAsync(async db => await db.DirectoryConnections.IgnoreQueryFilters().Where(d => d.Id == _connectionId).ExecuteDeleteAsync());

        User fry = (await UserAsync("fry"))!;
        Assert.Null(fry.DirectoryId);
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);   // nobody knows the password of the user now

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<UserService>().UpdateAsync(fry.Id, new UserInput { LoginName = "fry", DisplayName = "Philip Fry", Password = "A-Very-Long-Passw0rd" }));
        }

        Assert.True((await SignInAsync("fry", "A-Very-Long-Passw0rd")).Succeeded);
    }

    // ---- guessing ------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Five_wrong_passwords_for_somebody_who_is_no_user_yet_end_the_questions_to_the_directory()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        _directory.AddPerson("leela", "Turanga Leela", "leela@example.test");
        for (int i = 0; i < DirectoryAttemptLimiter.MaxWrongPasswords; i++)
        {
            Assert.False((await SignInAsync("fry", "wrong" + i)).Succeeded);
        }

        // the sixth: even the right password is not asked about (the lockout of the directory would count it)
        int binds = _directory.Binds;
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);
        Assert.Equal(binds, _directory.Binds);
        Assert.Null(await UserAsync("fry"));

        // another person is not affected, and the window moves on
        Assert.True((await SignInAsync("leela", "leela")).Succeeded);
        _clock.Advance(DirectoryAttemptLimiter.Window + TimeSpan.FromSeconds(1));
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
    }

    [DbFact]
    public async Task A_client_that_goes_through_the_names_is_kept_away_from_the_directory()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        for (int i = 0; i < DirectoryAttemptLimiter.MaxFailuresPerClient; i++)
        {
            Assert.False((await SignInAsync("nobody" + i, "x-" + i, ip: "192.0.2.99")).Succeeded);
        }

        int searches = _directory.Searches;
        Assert.False((await SignInAsync("fry", "fry", ip: "192.0.2.99")).Succeeded);
        Assert.Equal(searches, _directory.Searches);                                  // not even a search
        Assert.True((await SignInAsync("fry", "fry", ip: "192.0.2.100")).Succeeded);   // somebody else behind another address is fine
    }

    [DbFact]
    public async Task What_is_not_the_fault_of_the_client_is_not_counted()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");

        _directory.Down = true;
        for (int i = 0; i < DirectoryAttemptLimiter.MaxFailuresPerClient + 5; i++)
        {
            Assert.False((await SignInAsync("fry", "fry", ip: "192.0.2.50")).Succeeded);   // nobody can ask
        }

        for (int i = 0; i < DirectoryAttemptLimiter.MaxWrongPasswords + 5; i++)
        {
            Assert.False((await SignInAsync("fry", "", ip: "192.0.2.50")).Succeeded);   // no password at all: nothing that could lock anybody
        }

        _directory.Down = false;
        Assert.True((await SignInAsync("fry", "fry", ip: "192.0.2.50")).Succeeded);
    }

    [DbFact]
    public async Task A_person_who_signed_in_starts_with_a_clean_sheet()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        for (int i = 0; i < DirectoryAttemptLimiter.MaxWrongPasswords - 1; i++)
        {
            Assert.False((await SignInAsync("fry", "wrong" + i)).Succeeded);
        }

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);   // a user now: the lockout of the users counts from here, from zero
        for (int i = 0; i < DirectoryAttemptLimiter.MaxWrongPasswords - 1; i++)
        {
            Assert.False((await SignInAsync("fry", "wrong" + i)).Succeeded);
        }

        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
    }

    // ---- the comparison in the background ------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_comparison_says_whether_it_worked()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);

        await SyncAsync();
        DirectoryConnection ok = await _host.ReadAsync(db => db.DirectoryConnections.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        Assert.True(ok.LastSyncOk);
        Assert.NotNull(ok.LastSyncDate);

        _directory.Down = true;
        await SyncAsync();
        DirectoryConnection failed = await _host.ReadAsync(db => db.DirectoryConnections.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        Assert.False(failed.LastSyncOk);
        Assert.StartsWith("Not compared", failed.LastSyncMessage);
    }

    [DbFact]
    public async Task The_service_compares_only_the_directories_that_are_due()
    {
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        _directory.Remove(_directory.Entries.Single(e => e.First("uid") == "fry"));
        var service = new DirectorySyncService(_host.Services.GetRequiredService<IServiceScopeFactory>(), _host.Config, NullLogger<DirectorySyncService>.Instance);

        // compared a minute ago: not due (the interval is an hour)
        await _host.WriteAsync(async db => (await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync()).LastSyncDate = DateTime.UtcNow.AddMinutes(-1));
        await service.RunDueAsync(CancellationToken.None);
        Assert.Null((await UserAsync("fry"))!.DirectoryDisabledDate);

        // compared two hours ago: due, and the person who is gone is blocked
        await _host.WriteAsync(async db => (await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync()).LastSyncDate = DateTime.UtcNow.AddHours(-2));
        await service.RunDueAsync(CancellationToken.None);
        Assert.NotNull((await UserAsync("fry"))!.DirectoryDisabledDate);

        // never compared: due too; one that is switched off is left alone
        await _host.WriteAsync(async db =>
        {
            DirectoryConnection dir = await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync();
            dir.LastSyncDate = null;
            dir.IsActive = false;
        });
        _directory.AddPerson("fry", "Philip Fry", "fry@example.test");
        await _host.WriteAsync(async db => (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.LoginName == "fry")).DirectoryDisabledDate = null);
        await service.RunDueAsync(CancellationToken.None);
        Assert.Null((await UserAsync("fry"))!.DirectoryDisabledDate);
        Assert.Null((await _host.ReadAsync(db => db.DirectoryConnections.IgnoreQueryFilters().AsNoTracking().SingleAsync())).LastSyncDate);
    }
}

/// <summary>The limits against guessing at a directory, without a database.</summary>
public class DirectoryAttemptLimiterTests
{
    private readonly TestClock _clock = new();

    [Fact]
    public void A_login_is_blocked_after_five_wrong_passwords_and_the_window_moves_on()
    {
        var limiter = new DirectoryAttemptLimiter(_clock);
        for (int i = 0; i < 4; i++)
        {
            limiter.RecordWrongPassword("Fry", "10.0.0.1");
        }

        Assert.False(limiter.IsBlocked("fry", "10.0.0.1"));
        limiter.RecordWrongPassword("FRY", "10.0.0.1");
        Assert.True(limiter.IsBlocked("fry", "10.0.0.1"));      // the name counts without regard to case
        Assert.False(limiter.IsBlocked("leela", "10.0.0.1"));   // another name is free

        _clock.Advance(DirectoryAttemptLimiter.Window - TimeSpan.FromSeconds(1));
        Assert.True(limiter.IsBlocked("fry", "10.0.0.1"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(limiter.IsBlocked("fry", "10.0.0.1"));
    }

    [Fact]
    public void A_success_forgets_the_wrong_passwords_of_that_login_only()
    {
        var limiter = new DirectoryAttemptLimiter(_clock);
        for (int i = 0; i < 5; i++)
        {
            limiter.RecordWrongPassword("fry", null);
            limiter.RecordWrongPassword("leela", null);
        }

        limiter.Forget("fry");

        Assert.False(limiter.IsBlocked("fry", null));
        Assert.True(limiter.IsBlocked("leela", null));
    }

    [Fact]
    public void A_client_is_blocked_by_its_failures_over_all_names_and_nobody_else_is()
    {
        var limiter = new DirectoryAttemptLimiter(_clock);
        for (int i = 0; i < DirectoryAttemptLimiter.MaxFailuresPerClient - 1; i++)
        {
            limiter.RecordUnknown("10.0.0.1");
        }

        Assert.False(limiter.IsBlocked("anybody", "10.0.0.1"));
        limiter.RecordUnknown("10.0.0.1");
        Assert.True(limiter.IsBlocked("anybody", "10.0.0.1"));
        Assert.False(limiter.IsBlocked("anybody", "10.0.0.2"));
        Assert.False(limiter.IsBlocked("anybody", null));   // callers without an address (the system itself) are never limited by one
    }

    [Fact]
    public void Many_clients_and_names_do_not_pile_up()
    {
        var limiter = new DirectoryAttemptLimiter(_clock);
        for (int i = 0; i < 2000; i++)
        {
            limiter.RecordWrongPassword("name" + i, "10.1." + (i / 250) + "." + (i % 250));
        }

        _clock.Advance(DirectoryAttemptLimiter.Window + TimeSpan.FromMinutes(1));
        for (int i = 0; i < 600; i++)
        {
            limiter.RecordUnknown("10.9.9." + (i % 250));   // the pruning runs every few hundred operations
        }

        Assert.False(limiter.IsBlocked("name1", "10.1.0.1"));
    }
}

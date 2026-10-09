using System.Net;
using System.Net.Sockets;
using MatMail.Data;
using MatMail.Directories;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>How the LDAP client reaches a host: nothing but sockets, so no server is needed.</summary>
public class DirectoryConnectTests
{
    private static DirectorySettings Settings(string host, int port) => new(host, port, DirectorySecurity.None, false, null, null, 5);

    [Fact]
    public async Task A_name_with_several_addresses_is_connected_through_the_one_that_answers()
    {
        // "localhost" is ::1 and 127.0.0.1 (which one comes first depends on the system); only IPv4 is served here, and the library has no second try
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using IDirectoryClient client = new LdapDirectoryClientFactory().Create(Settings("localhost", ((IPEndPoint)listener.LocalEndpoint).Port));
        await client.ConnectAsync(CancellationToken.None);   // without an account there is no sign-in: opening is all it does
    }

    [Fact]
    public async Task A_host_that_refuses_the_connection_is_told_with_the_reason()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();   // now nobody is there

        using IDirectoryClient client = new LdapDirectoryClientFactory().Create(Settings("127.0.0.1", port));
        DirectoryException ex = await Assert.ThrowsAsync<DirectoryException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.StartsWith($"No connection to 127.0.0.1:{port}: ", ex.Message);
        Assert.DoesNotContain("Connect Error", ex.Message);
    }

    [Fact]
    public async Task A_name_that_does_not_exist_is_told()
    {
        using IDirectoryClient client = new LdapDirectoryClientFactory().Create(Settings("no-such-host.invalid", 389));

        DirectoryException ex = await Assert.ThrowsAsync<DirectoryException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.StartsWith("No connection to no-such-host.invalid:389: ", ex.Message);
    }
}

/// <summary>The LDAP client against a real server (see <see cref="TestLdap"/>).</summary>
public class DirectoryLdapTests
{
    private static readonly SecretProtector Secrets = new(new EphemeralDataProtectionProvider());

    private static DirectoryService Service() => new(new LdapDirectoryClientFactory(), Secrets);

    private static DirectoryConnection Connection(Action<DirectoryConnection>? change = null)
        => TestLdap.Connection(d =>
        {
            d.BindPasswordProtected = Secrets.Protect(TestLdap.AdminPassword);
            change?.Invoke(d);
        });

    [LdapFact]
    public async Task The_test_of_a_connection_finds_the_people_and_shows_a_few()
    {
        DirectoryCheck check = await Service().TestAsync(Connection());

        Assert.True(check.Ok, check.Message);
        Assert.Equal(7, check.Users);
        Assert.Equal(5, check.Sample.Count);
        Assert.Contains("7 people can sign in", check.Message);
    }

    [LdapFact]
    public async Task A_wrong_password_of_the_account_that_searches_is_told()
    {
        DirectoryCheck check = await Service().TestAsync(Connection(d => d.BindPasswordProtected = Secrets.Protect("not-the-password")));

        Assert.False(check.Ok);
        Assert.Contains("does not accept the account", check.Message);
    }

    [LdapFact]
    public async Task A_new_password_typed_on_the_page_is_tried_before_it_is_stored()
    {
        DirectoryCheck check = await Service().TestAsync(Connection(d => d.BindPasswordProtected = Secrets.Protect("old")), bindPassword: TestLdap.AdminPassword);

        Assert.True(check.Ok, check.Message);
    }

    [LdapFact]
    public async Task A_server_that_is_not_there_is_told()
    {
        DirectoryCheck check = await Service().TestAsync(Connection(d => { d.Host = "127.0.0.1"; d.Port = 1; }));

        Assert.False(check.Ok);
        Assert.Contains("No connection to 127.0.0.1:1", check.Message);
    }

    [LdapFact]
    public async Task A_base_that_does_not_exist_is_told()
    {
        DirectoryCheck check = await Service().TestAsync(Connection(d => d.BaseDn = "ou=nowhere,dc=planetexpress,dc=com"));

        Assert.False(check.Ok);
        Assert.Contains("does not exist", check.Message);
    }

    [LdapFact]
    public async Task A_login_finds_the_person_and_the_mapper_reads_the_entry()
    {
        DirectoryLookup found = await Service().FindUserAsync(Connection(), "FRY");

        DirectoryUser user = found.User!;
        Assert.Equal("fry", user.Login);
        Assert.Equal("cn=Philip J. Fry," + TestLdap.People, user.Dn);
        Assert.Equal("Philip J. Fry", user.DisplayName);
        Assert.Equal("fry@planetexpress.com", user.Email);
        Assert.Equal("Philip", user.FirstName);
        Assert.Equal("Fry", user.LastName);
        Assert.Equal("Delivery boy", user.JobTitle);
        Assert.False(string.IsNullOrEmpty(user.Uid));
        Assert.False(user.Disabled);
    }

    [LdapFact]
    public async Task Nobody_is_found_by_what_a_login_could_mean_to_a_filter()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();

        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "nobody"));
        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "*"));
        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "fr*"));
        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "fry)(uid=*"));
        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "x)(|(uid=fry)(uid=x"));
        Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, ""));
    }

    [LdapFact]
    public async Task The_directory_decides_about_a_password_and_an_empty_one_is_never_good()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();
        string fry = "cn=Philip J. Fry," + TestLdap.People;

        Assert.True(await service.VerifyPasswordAsync(dir, fry, "fry"));
        Assert.False(await service.VerifyPasswordAsync(dir, fry, "leela"));
        Assert.False(await service.VerifyPasswordAsync(dir, fry, ""));   // a bind without a password is an anonymous one that many servers accept
        Assert.False(await service.VerifyPasswordAsync(dir, "cn=Nobody," + TestLdap.People, "fry"));
        Assert.False(await service.VerifyPasswordAsync(dir, "", "fry"));
    }

    [LdapFact]
    public async Task Only_the_members_of_the_group_are_let_in_whichever_way_the_groups_are_read()
    {
        DirectoryService service = Service();
        foreach (GroupLookup lookup in new[] { GroupLookup.UserMemberOf, GroupLookup.GroupMembers })
        {
            DirectoryConnection dir = Connection(d => { d.AllowedGroupDn = TestLdap.ShipCrew; d.GroupLookup = lookup; });

            Assert.NotNull((await service.FindUserAsync(dir, "fry")).User);
            Assert.NotNull((await service.FindUserAsync(dir, "leela")).User);
            DirectoryLookup professor = await service.FindUserAsync(dir, "professor");
            Assert.Null(professor.User);
            Assert.True(professor.NotAllowed, lookup.ToString());
            Assert.Equal(DirectoryLookup.NotFound, await service.FindUserAsync(dir, "nobody"));

            DirectoryUser[] crew = (await service.SearchUsersAsync(dir, null, 100)).ToArray();
            Assert.Equal(new[] { "bender", "fry", "leela" }, crew.Select(u => u.Login).Order().ToArray());
        }
    }

    [LdapFact]
    public async Task A_search_lists_the_people_by_a_word_of_their_login_name_or_address()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();

        Assert.Equal(7, (await service.SearchUsersAsync(dir, null, 100)).Count);
        Assert.Equal(new[] { "zoidberg" }, (await service.SearchUsersAsync(dir, "zoid", 100)).Select(u => u.Login).ToArray());
        Assert.Equal(new[] { "leela" }, (await service.SearchUsersAsync(dir, "Turanga", 100)).Select(u => u.Login).ToArray());
        Assert.Equal(2, (await service.SearchUsersAsync(dir, "planetexpress", 2)).Count);   // the limit
        Assert.Empty(await service.SearchUsersAsync(dir, "*)(uid=*", 100));
    }

    [LdapFact]
    public async Task A_person_is_read_by_the_name_of_the_entry()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();

        Assert.Equal("fry", (await service.ReadUserAsync(dir, "cn=Philip J. Fry," + TestLdap.People))!.Login);
        Assert.Null(await service.ReadUserAsync(dir, "cn=Nobody," + TestLdap.People));
        Assert.Null(await service.ReadUserAsync(Connection(d => d.AllowedGroupDn = TestLdap.AdminStaff), "cn=Philip J. Fry," + TestLdap.People));   // not in that group
    }

    [LdapFact]
    public async Task The_connection_can_be_protected_by_ldaps_and_a_certificate_nobody_trusts_is_refused_unless_allowed()
    {
        DirectoryService service = Service();

        DirectoryCheck allowed = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.Ldaps; d.Port = TestLdap.LdapsPort; d.AllowInvalidCertificate = true; }));
        Assert.True(allowed.Ok, allowed.Message);

        DirectoryCheck refused = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.Ldaps; d.Port = TestLdap.LdapsPort; d.AllowInvalidCertificate = false; }));
        Assert.False(refused.Ok);
        Assert.Contains("No connection", refused.Message);
    }

    [LdapFact]
    public async Task Starttls_switches_the_connection_to_tls_before_anything_is_sent()
    {
        DirectoryService service = Service();

        DirectoryCheck allowed = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.StartTls; d.AllowInvalidCertificate = true; }));
        Assert.True(allowed.Ok, allowed.Message);

        DirectoryCheck refused = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.StartTls; d.AllowInvalidCertificate = false; }));
        Assert.False(refused.Ok);
    }
}

/// <summary>A sign-in with the password of a real directory (the whole way: search, bind, the user that comes of it).</summary>
public class DirectoryLdapSignInTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task AddConnectionAsync(Action<DirectoryConnection>? change = null)
    {
        using IServiceScope scope = _host.Scope();
        var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.DirectoryConnections.Add(TestLdap.Connection(d =>
        {
            d.TenantId = _seed.Tenant.Id;
            d.BindPasswordProtected = secrets.Protect(TestLdap.AdminPassword);
            d.CreateMailbox = false;
            change?.Invoke(d);
        }));
        await db.SaveChangesAsync();
    }

    private async Task<SignInOutcome> SignInAsync(string login, string password, SignInPurpose purpose = SignInPurpose.Web)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().ValidateCredentialsAsync(login, password, "192.0.2.7", purpose);
    }

    [LdapDbFact]
    public async Task A_person_of_the_directory_signs_in_with_their_password_and_gets_a_user()
    {
        await AddConnectionAsync();

        SignInOutcome outcome = await SignInAsync("fry", "fry");

        Assert.True(outcome.Succeeded);
        User user = await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.LoginName == "fry"));
        Assert.Equal("Philip J. Fry", user.DisplayName);
        Assert.Equal("fry@planetexpress.com", user.Email);
        Assert.Equal("Delivery boy", user.JobTitle);
        Assert.Equal(_seed.Tenant.Id, user.TenantId);
        Assert.False((await SignInAsync("fry", "wrong")).Succeeded);
        Assert.False((await SignInAsync("fry", "")).Succeeded);
        Assert.True((await SignInAsync("fry", "fry", SignInPurpose.Protocol)).Succeeded);
    }

    [LdapDbFact]
    public async Task Only_the_group_signs_in_and_the_others_are_not_made_users()
    {
        await AddConnectionAsync(d => d.AllowedGroupDn = TestLdap.ShipCrew);

        Assert.True((await SignInAsync("leela", "leela")).Succeeded);
        Assert.False((await SignInAsync("professor", "professor")).Succeeded);
        Assert.Equal(0, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.LoginName == "professor")));
    }

    [LdapDbFact]
    public async Task The_comparison_keeps_the_users_in_step_with_the_directory()
    {
        await AddConnectionAsync();
        Assert.True((await SignInAsync("fry", "fry")).Succeeded);
        Assert.True((await SignInAsync("professor", "professor")).Succeeded);

        // from now on only the admin staff is let in: fry is out, the professor stays
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        DirectoryConnection dir = await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync();
        dir.AllowedGroupDn = TestLdap.AdminStaff;
        await db.SaveChangesAsync();

        DirectorySyncResult result = await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().SyncAsync(dir);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Users);
        Assert.Equal(1, result.Blocked);
        Assert.Contains(result.Notes, n => n.StartsWith("fry "));
        Assert.False((await SignInAsync("fry", "fry")).Succeeded);
        Assert.True((await SignInAsync("professor", "professor")).Succeeded);
    }
}

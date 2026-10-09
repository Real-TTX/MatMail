using MatMail.Data;
using MatMail.Directories;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The directory code against a real Active Directory (a Samba domain controller, see <see cref="TestAd"/>).</summary>
public class DirectoryAdTests
{
    private static readonly SecretProtector Secrets = new(new EphemeralDataProtectionProvider());

    private static DirectoryService Service() => new(new LdapDirectoryClientFactory(), Secrets);

    private static DirectoryConnection Connection(Action<DirectoryConnection>? change = null)
        => TestAd.Connection(d =>
        {
            d.BindPasswordProtected = Secrets.Protect(TestAd.BindPassword);
            change?.Invoke(d);
        });

    private const string FryDn = "CN=Philip Fry,CN=Users,DC=example,DC=test";

    [AdFact]
    public async Task The_test_counts_the_people_who_can_sign_in_and_says_how_many_accounts_are_switched_off()
    {
        DirectoryCheck check = await Service().TestAsync(Connection());

        Assert.True(check.Ok, check.Message);
        Assert.True(check.Users >= 4, check.Message);   // Administrator, fry, leela, zoidberg and the account that searches
        Assert.Contains(check.Notes, n => n.Contains("disabled in the directory"));   // Guest, krbtgt and bender
        Assert.DoesNotContain(check.Sample, p => p.Disabled);
    }

    [AdFact]
    public async Task A_person_is_read_with_the_attributes_of_active_directory()
    {
        DirectoryUser fry = (await Service().FindUserAsync(Connection(), "FRY")).User!;

        Assert.Equal("fry", fry.Login);
        Assert.Equal(FryDn, fry.Dn, ignoreCase: true);
        Assert.Equal("Philip Fry", fry.DisplayName);
        Assert.Equal("fry@example.test", fry.Email);
        Assert.Equal("Philip", fry.FirstName);
        Assert.Equal("Fry", fry.LastName);
        Assert.Equal("Delivery boy", fry.JobTitle);
        Assert.Equal("Delivery", fry.Department);
        Assert.Equal("+49 721 1", fry.Phone);
        Assert.False(fry.Disabled);

        // objectGUID is binary: it becomes the GUID that Active Directory shows, and the same one at every look
        Assert.True(Guid.TryParse(fry.Uid, out Guid guid), fry.Uid);
        Assert.NotEqual(Guid.Empty, guid);
        Assert.Equal(fry.Uid, (await Service().FindUserAsync(Connection(), "fry")).User!.Uid);
    }

    [AdFact]
    public async Task The_user_principal_name_works_as_the_login_too()
    {
        DirectoryConnection dir = Connection(d => d.LoginAttribute = "userPrincipalName");

        DirectoryUser fry = (await Service().FindUserAsync(dir, "Fry@Example.Test")).User!;

        Assert.Equal("fry@example.test", fry.Login);
        Assert.Equal(FryDn, fry.Dn, ignoreCase: true);
    }

    [AdFact]
    public async Task A_switched_off_account_is_found_but_marked_and_its_password_is_refused_by_the_directory()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();

        DirectoryUser bender = (await service.FindUserAsync(dir, "bender")).User!;

        Assert.True(bender.Disabled);   // userAccountControl bit 2
        Assert.False(await service.VerifyPasswordAsync(dir, bender.Dn, "Bender-Passw0rd!1"));   // data 533: the right password of an account that is off
    }

    [AdFact]
    public async Task The_password_is_checked_by_signing_in_as_the_person()
    {
        DirectoryService service = Service();
        DirectoryConnection dir = Connection();
        string fry = (await service.FindUserAsync(dir, "fry")).User!.Dn;

        Assert.True(await service.VerifyPasswordAsync(dir, fry, "Fry-Passw0rd!1"));
        Assert.False(await service.VerifyPasswordAsync(dir, fry, "Leela-Passw0rd!1"));   // data 52e
        Assert.False(await service.VerifyPasswordAsync(dir, fry, ""));
        Assert.False(await service.VerifyPasswordAsync(dir, fry + ",DC=nowhere", "Fry-Passw0rd!1"));
    }

    [AdFact]
    public async Task A_group_decides_through_memberOf_and_through_groups_inside_it_only_when_asked()
    {
        DirectoryService service = Service();

        // crew: fry, leela and bender are its members
        DirectoryConnection crew = Connection(d => d.AllowedGroupDn = TestAd.Crew);
        Assert.NotNull((await service.FindUserAsync(crew, "fry")).User);
        Assert.NotNull((await service.FindUserAsync(crew, "leela")).User);
        DirectoryLookup zoidberg = await service.FindUserAsync(crew, "zoidberg");
        Assert.Null(zoidberg.User);
        Assert.True(zoidberg.NotAllowed);

        // fleet: crew is a member of it, so the people are in it through crew – the plain memberOf does not see that, the in-chain rule does
        DirectoryConnection direct = Connection(d => d.AllowedGroupDn = TestAd.Fleet);
        Assert.True((await service.FindUserAsync(direct, "fry")).NotAllowed);
        DirectoryConnection nested = Connection(d => { d.AllowedGroupDn = TestAd.Fleet; d.NestedGroups = true; });
        Assert.NotNull((await service.FindUserAsync(nested, "fry")).User);
        Assert.True((await service.FindUserAsync(nested, "zoidberg")).NotAllowed);

        string[] inFleet = (await service.SearchUsersAsync(nested, null, 100)).Select(u => u.Login).Order().ToArray();
        Assert.Equal(new[] { "bender", "fry", "leela" }, inFleet);
    }

    [AdFact]
    public async Task The_member_list_of_a_group_works_in_active_directory_too()
    {
        DirectoryConnection dir = Connection(d => { d.AllowedGroupDn = TestAd.Crew; d.GroupLookup = GroupLookup.GroupMembers; });
        DirectoryService service = Service();

        Assert.NotNull((await service.FindUserAsync(dir, "leela")).User);
        Assert.True((await service.FindUserAsync(dir, "zoidberg")).NotAllowed);
    }

    [AdFact]
    public async Task A_search_over_the_whole_domain_goes_past_the_referrals_to_the_other_partitions()
    {
        // the answer of a search at the root ends with ldap://…/CN=Configuration,… and the DNS zones: they are no people
        IReadOnlyList<DirectoryUser> people = await Service().SearchUsersAsync(Connection(), null, 100);

        Assert.Contains(people, p => p.Login == "fry");
        Assert.Contains(people, p => p.Login == "zoidberg");
        Assert.All(people, p => Assert.False(string.IsNullOrWhiteSpace(p.Login)));
    }

    [AdFact]
    public async Task The_account_that_searches_with_a_wrong_password_is_told()
    {
        DirectoryCheck check = await Service().TestAsync(Connection(d => d.BindPasswordProtected = Secrets.Protect("not-it")));

        Assert.False(check.Ok);
        Assert.Contains("does not accept the account", check.Message);
    }

    [AdFact]
    public async Task The_connection_can_be_encrypted_with_starttls_or_ldaps_and_the_certificate_of_the_server_is_its_own()
    {
        DirectoryService service = Service();

        DirectoryCheck startTls = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.StartTls; d.AllowInvalidCertificate = true; }));
        Assert.True(startTls.Ok, startTls.Message);

        DirectoryCheck ldaps = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.Ldaps; d.Port = TestAd.LdapsPort; d.AllowInvalidCertificate = true; }));
        Assert.True(ldaps.Ok, ldaps.Message);

        DirectoryCheck refused = await service.TestAsync(Connection(d => { d.Security = DirectorySecurity.Ldaps; d.Port = TestAd.LdapsPort; d.AllowInvalidCertificate = false; }));
        Assert.False(refused.Ok);
        Assert.Contains("certificate", refused.Message, StringComparison.OrdinalIgnoreCase);   // not "Connect Error"
    }
}

/// <summary>People of an Active Directory sign in to MatMail (the whole way: search, bind, the user that comes of it).</summary>
public class DirectoryAdSignInTests : IAsyncLifetime
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
        db.DirectoryConnections.Add(TestAd.Connection(d =>
        {
            d.TenantId = _seed.Tenant.Id;
            d.BindPasswordProtected = secrets.Protect(TestAd.BindPassword);
            d.CreateMailbox = false;
            change?.Invoke(d);
        }));
        await db.SaveChangesAsync();
    }

    private async Task<SignInOutcome> SignInAsync(string login, string password)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().ValidateCredentialsAsync(login, password, "192.0.2.8", SignInPurpose.Web);
    }

    [AdDbFact]
    public async Task A_person_of_the_domain_signs_in_with_the_domain_password_and_gets_a_user_with_the_guid()
    {
        await AddConnectionAsync();

        Assert.False((await SignInAsync("fry", "Leela-Passw0rd!1")).Succeeded);
        Assert.True((await SignInAsync("FRY", "Fry-Passw0rd!1")).Succeeded);

        User fry = await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.LoginName == "fry"));
        Assert.Equal("Philip Fry", fry.DisplayName);
        Assert.Equal("fry@example.test", fry.Email);
        Assert.Equal("Delivery boy", fry.JobTitle);
        Assert.Equal("Delivery", fry.Department);
        Assert.True(Guid.TryParse(fry.DirectoryUid, out _));
        Assert.Equal(_seed.Tenant.Id, fry.TenantId);
        Assert.True((await SignInAsync("fry", "Fry-Passw0rd!1")).Succeeded);   // the second time it is that user
    }

    [AdDbFact]
    public async Task A_switched_off_account_does_not_sign_in_and_is_not_made_a_user()
    {
        await AddConnectionAsync();

        Assert.False((await SignInAsync("bender", "Bender-Passw0rd!1")).Succeeded);
        Assert.Equal(0, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.LoginName == "bender")));
    }

    [AdDbFact]
    public async Task Only_the_members_of_the_group_sign_in_also_through_groups_inside_it()
    {
        await AddConnectionAsync(d => { d.AllowedGroupDn = TestAd.Fleet; d.NestedGroups = true; });

        Assert.True((await SignInAsync("leela", "Leela-Passw0rd!1")).Succeeded);
        Assert.False((await SignInAsync("zoidberg", "Zoidberg-Passw0rd!1")).Succeeded);
        Assert.Equal(0, await _host.ReadAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.LoginName == "zoidberg")));
    }

    [AdDbFact]
    public async Task The_comparison_lets_go_of_whoever_the_group_no_longer_holds()
    {
        await AddConnectionAsync();
        Assert.True((await SignInAsync("fry", "Fry-Passw0rd!1")).Succeeded);
        Assert.True((await SignInAsync("zoidberg", "Zoidberg-Passw0rd!1")).Succeeded);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        DirectoryConnection dir = await db.DirectoryConnections.IgnoreQueryFilters().SingleAsync();
        dir.AllowedGroupDn = TestAd.Crew;   // zoidberg is not in it
        await db.SaveChangesAsync();

        DirectorySyncResult result = await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().SyncAsync(dir);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Blocked);
        Assert.Contains(result.Notes, n => n.StartsWith("zoidberg "));
        Assert.False((await SignInAsync("zoidberg", "Zoidberg-Passw0rd!1")).Succeeded);
        Assert.True((await SignInAsync("fry", "Fry-Passw0rd!1")).Succeeded);
    }
}

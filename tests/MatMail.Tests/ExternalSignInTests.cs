using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>
/// How a person signed in by MatIdentity becomes a local user (<see cref="ExternalSignInService"/>): by <c>sub</c>, else the one user with the
/// address, else a new one; the claim <c>role</c>; and that IMAP and SMTP keep signing the user in exactly as before.
/// </summary>
public sealed class ExternalSignInTests : IAsyncLifetime
{
    private const string Password = "Test-Passw0rd!";

    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<ExternalSignInResult> SignInAsync(string subject, string? email, string? name = null, string? preferred = null, string? role = null)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<ExternalSignInService>()
            .SignInAsync(new ExternalPerson(subject, email, name, preferred, role));
    }

    private async Task<User> LoadAsync(long userId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private async Task<string[]> RoleNamesAsync(long userId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().UserRoles
            .Where(r => r.UserId == userId)
            .Select(r => r.Role!.Name)
            .OrderBy(n => n)
            .ToArrayAsync();
    }

    private async Task<MailUser?> ProtocolLoginAsync(string login, string secret)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync(login, secret, "127.0.0.1");
    }

    [DbFact]
    public async Task A_new_person_becomes_a_user_of_the_first_tenant_and_is_found_again_by_sub()
    {
        ExternalSignInResult first = await SignInAsync("sub-1", "Nora@Elsewhere.Example", "Nora Neu", "nora");

        Assert.True(first.Succeeded, first.Error);
        User nora = await LoadAsync(first.User!.Id);
        Assert.Equal("sub-1", nora.ExternalSubject);
        Assert.Equal("nora@elsewhere.example", nora.LoginName);
        Assert.Equal("nora@elsewhere.example", nora.Email);
        Assert.Equal("Nora Neu", nora.DisplayName);
        Assert.Equal(_seed.Tenant.Id, nora.TenantId);
        Assert.False(nora.IsSystemAdmin);
        Assert.Equal([TenantService.UserRoleName], await RoleNamesAsync(nora.Id));
        Assert.False(string.IsNullOrEmpty(nora.PasswordHash));

        // Nobody knows the password of such a user: the mail protocols do not let a guess in.
        Assert.Null(await ProtocolLoginAsync("nora@elsewhere.example", Password));

        ExternalSignInResult again = await SignInAsync("sub-1", "changed@elsewhere.example", "Nora");
        Assert.Equal(nora.Id, again.User!.Id);
    }

    [DbFact]
    public async Task Without_an_address_the_preferred_user_name_is_the_login_and_a_taken_one_gets_a_number()
    {
        ExternalSignInResult first = await SignInAsync("sub-a", null, null, "Alice");
        ExternalSignInResult second = await SignInAsync("sub-b", null, null, null);

        Assert.Equal("alice-2", (await LoadAsync(first.User!.Id)).LoginName);   // "alice" is the user of the seed
        Assert.Equal("Alice", first.User!.DisplayName);
        Assert.StartsWith("matidentity-sub", (await LoadAsync(second.User!.Id)).LoginName);
    }

    [DbFact]
    public async Task The_owner_of_the_mailbox_with_the_address_is_linked_and_keeps_signing_in_to_mail_programs()
    {
        string hashBefore = (await LoadAsync(_seed.Alice.Id)).PasswordHash;

        ExternalSignInResult result = await SignInAsync("sub-alice", $"ALICE@{TestHost.Domain}", "Alice Example");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(_seed.Alice.Id, result.User!.Id);
        User alice = await LoadAsync(_seed.Alice.Id);
        Assert.Equal("sub-alice", alice.ExternalSubject);
        Assert.Equal("alice", alice.LoginName);
        Assert.Equal("Alice", alice.DisplayName);
        Assert.Equal(hashBefore, alice.PasswordHash);

        // IMAP and SMTP: the login name and the address with the password, as before.
        Assert.NotNull(await ProtocolLoginAsync("alice", Password));
        Assert.NotNull(await ProtocolLoginAsync($"alice@{TestHost.Domain}", Password));
    }

    [DbFact]
    public async Task An_address_of_two_users_or_of_another_person_is_not_linked()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            User bob = await db.Users.SingleAsync(u => u.Id == _seed.Bob.Id);
            bob.Email = $"alice@{TestHost.Domain}";
            await db.SaveChangesAsync();
        }

        ExternalSignInResult twoUsers = await SignInAsync("sub-x", $"alice@{TestHost.Domain}");
        Assert.False(twoUsers.Succeeded);
        Assert.Null((await LoadAsync(_seed.Alice.Id)).ExternalSubject);

        Assert.True((await SignInAsync("sub-bob", $"bob@{TestHost.Domain}")).Succeeded);
        ExternalSignInResult taken = await SignInAsync("sub-other", $"bob@{TestHost.Domain}");
        Assert.False(taken.Succeeded);
        Assert.Equal("sub-bob", (await LoadAsync(_seed.Bob.Id)).ExternalSubject);
    }

    [DbFact]
    public async Task The_role_claim_is_followed_at_every_sign_in_and_without_it_nothing_changes()
    {
        long id = (await SignInAsync("sub-r", "r@elsewhere.example", "R", null, "tenant-admin")).User!.Id;
        Assert.Equal([TenantService.AdministratorRoleName, TenantService.UserRoleName], await RoleNamesAsync(id));
        Assert.False((await LoadAsync(id)).IsSystemAdmin);

        await SignInAsync("sub-r", null, "R", null, "admin");
        Assert.True((await LoadAsync(id)).IsSystemAdmin);

        await SignInAsync("sub-r", null, "R", null, null);
        await SignInAsync("sub-r", null, "R", null, "something-else");
        Assert.True((await LoadAsync(id)).IsSystemAdmin);

        // Demoted: the seed has no other system administrator, so the first "user" keeps it, a second administrator lets it go.
        await SignInAsync("sub-r", null, "R", null, "user");
        Assert.True((await LoadAsync(id)).IsSystemAdmin);
        Assert.Equal([TenantService.UserRoleName], await RoleNamesAsync(id));

        await SignInAsync("sub-second", "second@elsewhere.example", "Second", null, "admin");
        await SignInAsync("sub-r", null, "R", null, "user");
        Assert.False((await LoadAsync(id)).IsSystemAdmin);
        Assert.Equal([TenantService.UserRoleName], await RoleNamesAsync(id));
    }

    [DbFact]
    public async Task A_disabled_user_stays_out()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            (await db.Users.SingleAsync(u => u.Id == _seed.Bob.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.False((await SignInAsync("sub-bob", $"bob@{TestHost.Domain}")).Succeeded);
    }

    [DbFact]
    public async Task No_subject_is_refused()
        => Assert.False((await SignInAsync("  ", "x@elsewhere.example")).Succeeded);
}

using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>
/// The account-linking proof rule of MatPlatform 1.1.0 (<see cref="MatIdentityPendingLink"/>). The attack it guards against: somebody puts the
/// address of another person into their own MatMail account (contact address, login name or a mailbox address); at that person's first sign-in
/// through MatIdentity they would land in the account of the one who typed it. So an account that matches by its address is linked only after
/// its own sign-in here – password, and the code when it has a second factor –; another account signing in links nothing.
/// </summary>
[Collection(PlatformWebCollection.Name)]
public sealed class PendingLinkTests(IdentityPlatformFactory site) : IClassFixture<IdentityPlatformFactory>
{
    private const string LinkTarget = "/Account/Login?local=1&link=1";
    private const string Password = "Pending-Passw0rd!";

    [DbFact]
    public async Task A_pre_claimed_address_does_not_let_the_person_into_that_account()
    {
        User mallory = await AddUserAsync("mallory", "victim@pending.example");
        int count = await CountUsersAsync();
        var browser = new IdentityBrowser(site);

        string target = await browser.SignInThroughIdentityAsync(Person("sub-victim", "victim@pending.example", "admin"));

        Assert.Equal(LinkTarget, target);
        Assert.Null((await FindAsync(mallory.Id)).ExternalSubject);
        Assert.False((await FindAsync(mallory.Id)).IsSystemAdmin);
        Assert.Equal(count, await CountUsersAsync());
        Assert.True(browser.HasPendingLink);

        // Signed in as nobody.
        HttpResponseMessage admin = await browser.Client.GetAsync("/Account");
        Assert.Equal(HttpStatusCode.Redirect, admin.StatusCode);
        Assert.Contains("/Account/Login", admin.Headers.Location!.OriginalString);

        string page = await browser.Client.GetStringAsync(LinkTarget);
        Assert.Contains("id=\"login-link\"", page);
        Assert.Contains("id=\"login-form\"", page);
        Assert.Contains("id=\"login-link-cancel\"", page);
    }

    [DbFact]
    public async Task The_local_sign_in_of_the_matched_account_links_it()
    {
        User vera = await AddUserAsync("vera", "vera@pending.example");
        var browser = new IdentityBrowser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-vera", "vera@pending.example", "tenant-admin")));

        HttpResponseMessage response = await browser.SignInLocallyAsync("vera", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("sub-vera", (await FindAsync(vera.Id)).ExternalSubject);
        Assert.Contains(TenantService.AdministratorRoleName, await RoleNamesAsync(vera.Id));
        Assert.False(browser.HasPendingLink);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Account")).StatusCode);
        Assert.True(await LoggedAsync(vera.Id, "linked with MatIdentity"));

        // The session keeps the ID token of the person: the sign-out ends the session at MatIdentity too.
        Assert.Equal("sub-vera.id.token", await IdentityTokenOfSessionAsync(vera.Id));
        Assert.Equal("sub-vera.sid", await QueryAsync(db => db.UserSessions.Where(s => s.UserId == vera.Id).Select(s => s.IdentitySessionId).FirstAsync()));

        // The next sign-in through MatIdentity goes straight into the account.
        Assert.Equal("/", await new IdentityBrowser(site).SignInThroughIdentityAsync(Person("sub-vera", "vera@pending.example", null)));
    }

    [DbFact]
    public async Task With_a_second_factor_the_account_is_linked_only_after_the_code()
    {
        User tom = await AddUserAsync("tom", "tom@pending.example");
        byte[] key = await EnrolAsync(tom.Id);
        var browser = new IdentityBrowser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-tom", "tom@pending.example", null)));

        // The password alone is no proof yet.
        HttpResponseMessage password = await browser.SignInLocallyAsync("tom", Password);
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        Assert.Equal("/Account/TwoFactor", password.Headers.Location!.OriginalString);
        Assert.Null((await FindAsync(tom.Id)).ExternalSubject);
        Assert.True(browser.HasPendingLink);

        string page = await browser.Client.GetStringAsync("/Account/TwoFactor");
        HttpResponseMessage code = await browser.Client.PostAsync("/Account/TwoFactor", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = IdentityBrowser.Token(page),
            ["Input.Code"] = Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow) + 1),
        }));

        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
        Assert.Equal("sub-tom", (await FindAsync(tom.Id)).ExternalSubject);
        Assert.False(browser.HasPendingLink);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Account")).StatusCode);
    }

    [DbFact]
    public async Task Another_account_signing_in_links_nothing()
    {
        User walter = await AddUserAsync("walter", "walter@pending.example");
        await AddUserAsync("other", "other@pending.example");
        var browser = new IdentityBrowser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-walter", "walter@pending.example", "admin")));

        HttpResponseMessage response = await browser.SignInLocallyAsync("other", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null((await FindAsync(walter.Id)).ExternalSubject);
        Assert.False(await AnyWithSubjectAsync("sub-walter"));
        Assert.False(browser.HasPendingLink);

        // The link is gone: Walter's own sign-in afterwards links nothing either.
        await new IdentityBrowser(site).SignInLocallyAsync("walter", Password);
        Assert.Null((await FindAsync(walter.Id)).ExternalSubject);
    }

    [DbFact]
    public async Task Cancel_forgets_the_pending_link()
    {
        User clara = await AddUserAsync("clara", "clara@pending.example");
        var browser = new IdentityBrowser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-clara", "clara@pending.example", null)));

        string page = await browser.Client.GetStringAsync(LinkTarget);
        HttpResponseMessage cancel = await browser.Client.PostAsync("/Account/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = IdentityBrowser.Token(page),
        }));

        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.Equal("/Account/Login?local=1", cancel.Headers.Location!.OriginalString);
        Assert.False(browser.HasPendingLink);
        Assert.DoesNotContain("id=\"login-link\"", await browser.Client.GetStringAsync(LinkTarget));

        await browser.SignInLocallyAsync("clara", Password);
        Assert.Null((await FindAsync(clara.Id)).ExternalSubject);
    }

    [DbFact]
    public async Task An_expired_or_forged_pending_link_is_ignored()
    {
        User erik = await AddUserAsync("erik", "erik@pending.example");
        var link = new MatIdentityPendingLink("sub-erik", "erik@pending.example", "Erik", null, "admin", null, erik.Id.ToString(CultureInfo.InvariantCulture));
        string json = JsonSerializer.Serialize(link, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var keys = site.Services.GetRequiredService<IDataProtectionProvider>();
        ITimeLimitedDataProtector protector = keys.CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector();

        string[] values =
        [
            protector.Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1)),
            "CfDJ8forged-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)),
            keys.CreateProtector("Another.Purpose").ToTimeLimitedDataProtector().Protect(json, TimeSpan.FromMinutes(5)),
        ];

        foreach (string value in values)
        {
            var browser = new IdentityBrowser(site);
            browser.SetPendingLink(value);

            string page = await browser.Client.GetStringAsync(LinkTarget);
            Assert.DoesNotContain("id=\"login-link\"", page);
            Assert.Contains("id=\"login-link-expired\"", page);

            Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInLocallyAsync("erik", Password)).StatusCode);
            Assert.Null((await FindAsync(erik.Id)).ExternalSubject);
            Assert.False((await FindAsync(erik.Id)).IsSystemAdmin);
        }

        // The same link with time left does link: the values above failed for their age, forgery or purpose only.
        var fresh = new IdentityBrowser(site);
        fresh.SetPendingLink(protector.Protect(json, DateTimeOffset.UtcNow.AddMinutes(5)));
        await fresh.SignInLocallyAsync("erik", Password);
        Assert.Equal("sub-erik", (await FindAsync(erik.Id)).ExternalSubject);
        Assert.True((await FindAsync(erik.Id)).IsSystemAdmin);
    }

    private static MatIdentityPerson Person(string subject, string email, string? role)
        => new(subject, email, subject, null, role, subject + ".id.token", new ClaimsPrincipal()) { SessionId = subject + ".sid" };

    private async Task<User> AddUserAsync(string login, string email)
    {
        using IServiceScope scope = site.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long tenantId = await db.Tenants.IgnoreQueryFilters().OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();
        (User? user, string? error) = await scope.ServiceProvider.GetRequiredService<UserService>().CreateAsync(
            new UserInput { LoginName = login, DisplayName = login, Email = email, Password = Password, CreateMailbox = false },
            tenantId);
        Assert.Null(error);
        return user!;
    }

    /// <summary>Turns two-factor authentication on for the user, like the account page does; answers the key of the authenticator.</summary>
    private async Task<byte[]> EnrolAsync(long userId)
    {
        using IServiceScope scope = site.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        (string? error, string? secret) = await service.BeginEnrolmentAsync(userId);
        Assert.Null(error);
        Assert.True(Base32.TryDecode(secret, out byte[] key));
        (error, _) = await service.ConfirmEnrolmentAsync(userId, Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow)), null, "127.0.0.1");
        Assert.Null(error);
        return key;
    }

    private async Task<T> QueryAsync<T>(Func<MatMailDbContext, Task<T>> query)
    {
        using IServiceScope scope = site.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        return await query(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    private Task<User> FindAsync(long id) => QueryAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<int> CountUsersAsync() => QueryAsync(db => db.Users.IgnoreQueryFilters().CountAsync());

    private Task<bool> AnyWithSubjectAsync(string subject) => QueryAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject));

    private Task<bool> LoggedAsync(long userId, string text)
        => QueryAsync(db => db.ActivityLogs.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId && a.Message.Contains(text)));

    private Task<string?> IdentityTokenOfSessionAsync(long userId)
        => QueryAsync(db => db.UserSessions.IgnoreQueryFilters().Where(s => s.UserId == userId).OrderByDescending(s => s.Id).Select(s => s.IdentityToken).FirstOrDefaultAsync());

    private Task<string[]> RoleNamesAsync(long userId)
        => QueryAsync(db => db.UserRoles.IgnoreQueryFilters().Where(r => r.UserId == userId).Select(r => r.Role!.Name).ToArrayAsync());
}

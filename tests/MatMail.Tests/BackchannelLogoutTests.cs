using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using MatPlatform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatMail.Tests;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0): <c>POST /signout-backchannel</c> with a signed <c>logout+jwt</c> ends MatMail's
/// sessions of the person (<see cref="IdentitySessionService"/>). A session that started with the <c>sid</c> of the token ends, another one of the
/// same person stays; a token with only <c>sub</c> ends every session of the linked user; a token with a wrong key is 400 and ends nothing; the
/// ticket of the code page cannot make a session for a <c>sid</c> that ended.
/// </summary>
[Collection(PlatformWebCollection.Name)]
public sealed class BackchannelLogoutTests(IdentityPlatformFactory site) : IClassFixture<IdentityPlatformFactory>
{
    private const string Path = "/signout-backchannel";

    [DbFact]
    public async Task A_valid_token_with_a_sid_ends_that_session_and_the_other_one_survives()
    {
        var first = new IdentityBrowser(site);
        var second = new IdentityBrowser(site);
        Assert.Equal("/", await first.SignInThroughIdentityAsync(Person("sub-anna", "anna@backchannel.example", "sid-anna-1")));
        Assert.Equal("/", await second.SignInThroughIdentityAsync(Person("sub-anna", "anna@backchannel.example", "sid-anna-2")));
        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync("/Account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/Account")).StatusCode);
        Assert.Equal(["sid-anna-1", "sid-anna-2"], await SessionIdsAsync("sub-anna"));

        HttpResponseMessage response = await PostAsync(Token("sub-anna", "sid-anna-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await AssertSignedOutAsync(first);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/Account")).StatusCode);
        Assert.Equal(["sid-anna-2"], await SessionIdsAsync("sub-anna"));
        Assert.True(await LoggedAsync("sub-anna", "MatIdentity: 'anna@backchannel.example' signed out"));
    }

    [DbFact]
    public async Task A_token_with_only_the_subject_ends_every_session_of_the_user_and_nobody_else()
    {
        var first = new IdentityBrowser(site);
        var second = new IdentityBrowser(site);
        var other = new IdentityBrowser(site);
        await first.SignInThroughIdentityAsync(Person("sub-ben", "ben@backchannel.example", "sid-ben-1"));
        await second.SignInThroughIdentityAsync(Person("sub-ben", "ben@backchannel.example", null));
        await other.SignInThroughIdentityAsync(Person("sub-cleo", "cleo@backchannel.example", "sid-cleo"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-ben", null))).StatusCode);

        await AssertSignedOutAsync(first);
        await AssertSignedOutAsync(second);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/Account")).StatusCode);
        Assert.Empty(await SessionIdsAsync("sub-ben"));
        Assert.True(await LoggedAsync("sub-ben", "signed out everywhere"));
    }

    [DbFact]
    public async Task A_token_signed_with_another_key_is_400_and_ends_nothing()
    {
        var browser = new IdentityBrowser(site);
        await browser.SignInThroughIdentityAsync(Person("sub-dora", "dora@backchannel.example", "sid-dora"));
        var forger = new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "matidentity-test" }, SecurityAlgorithms.RsaSha256);

        HttpResponseMessage response = await PostAsync(Token("sub-dora", "sid-dora", forger));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Account")).StatusCode);
        Assert.Equal(["sid-dora"], await SessionIdsAsync("sub-dora"));
    }

    [DbFact]
    public async Task An_unknown_person_or_session_is_no_error()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-nobody", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-nobody"))).StatusCode);
    }

    [DbFact]
    public async Task A_sign_in_that_waits_for_the_code_makes_no_session_once_its_sid_ended()
    {
        var setup = new IdentityBrowser(site);
        await setup.SignInThroughIdentityAsync(Person("sub-emil", "emil@backchannel.example", "sid-emil-0"));
        long emil = await UserIdAsync("sub-emil");
        byte[] key = await EnrolAsync(emil);

        // MatIdentity signed the person in, the code is still to come: the ticket carries the sid.
        var browser = new IdentityBrowser(site);
        await browser.SignInThroughIdentityAsync(Person("sub-emil", "emil@backchannel.example", "sid-emil-1"));
        string page = await browser.Client.GetStringAsync("/Account/TwoFactor");

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-emil", "sid-emil-1"))).StatusCode);

        HttpResponseMessage code = await browser.Client.PostAsync("/Account/TwoFactor", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = IdentityBrowser.Token(page),
            ["Input.Code"] = Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow) + 1),
        }));

        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);
        Assert.Contains("/Account/Login", code.Headers.Location!.OriginalString);
        await AssertSignedOutAsync(browser);
        Assert.DoesNotContain("sid-emil-1", await SessionIdsAsync("sub-emil"));
    }

    private static MatIdentityPerson Person(string subject, string email, string? sessionId)
        => new(subject, email, subject, null, null, subject + ".id.token", new ClaimsPrincipal()) { SessionId = sessionId };

    /// <summary>A logout token as MatIdentity writes it (OpenID Connect Back-Channel Logout 1.0).</summary>
    private static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityPlatformFactory.Authority,
            ["aud"] = "matmail",
            ["iat"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? PlatformFactory.SigningCredentials,
        });
    }

    private Task<HttpResponseMessage> PostAsync(string token)
        => site.CreateClient().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    private static async Task AssertSignedOutAsync(IdentityBrowser browser)
    {
        HttpResponseMessage response = await browser.Client.GetAsync("/Account");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString);
    }

    private async Task<T> QueryAsync<T>(Func<MatMailDbContext, Task<T>> query)
    {
        using IServiceScope scope = site.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        return await query(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    private Task<long> UserIdAsync(string subject)
        => QueryAsync(db => db.Users.IgnoreQueryFilters().Where(u => u.ExternalSubject == subject).Select(u => u.Id).SingleAsync());

    /// <summary>The sids of the sessions of the person, "-" for a session without one.</summary>
    private Task<string[]> SessionIdsAsync(string subject)
        => QueryAsync(db => db.UserSessions.Where(s => s.User!.ExternalSubject == subject).OrderBy(s => s.IdentitySessionId).Select(s => s.IdentitySessionId ?? "-").ToArrayAsync());

    private Task<bool> LoggedAsync(string subject, string text)
        => QueryAsync(async db =>
        {
            long id = await db.Users.IgnoreQueryFilters().Where(u => u.ExternalSubject == subject).Select(u => u.Id).SingleAsync();
            return await db.ActivityLogs.IgnoreQueryFilters().AnyAsync(a => a.UserId == id && a.Message.Contains(text));
        });

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
}

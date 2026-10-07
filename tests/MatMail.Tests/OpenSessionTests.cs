using System.Security.Claims;
using System.Text;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>What happens to a mail program that is signed in when the way it signed in stops counting.</summary>
internal static class OpenSessionSupport
{
    /// <summary>Two-factor authentication is on for the user (a confirmed authenticator; its secret is irrelevant here).</summary>
    public static async Task TurnOnTwoFactorAsync(TestHost host, long userId)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.UserTotps.Add(new UserTotp { UserId = userId, Secret = "not-used", ConfirmedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public static async Task RequireTwoFactorForEveryoneAsync(TestHost host, long tenantId)
    {
        using IServiceScope scope = host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Tenants
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TwoFactorMode, TwoFactorMode.Everyone));
    }

    /// <summary>A new app password of the user, as the person would copy it.</summary>
    public static async Task<string> CreateAppPasswordAsync(TestHost host, User user)
    {
        using IServiceScope scope = host.Scope();
        (string? error, string? password) = await scope.ServiceProvider.GetRequiredService<AppPasswordService>().CreateAsync(user, "Phone");
        Assert.Null(error);
        return password!;
    }

    public static async Task RevokeAppPasswordsAsync(TestHost host, long userId)
    {
        using IServiceScope scope = host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().AppPasswords.Where(a => a.UserId == userId).ExecuteDeleteAsync();
    }
}

public class OpenImapSessionTests : ImapTestBase
{
    private async Task<RawImapClient> LoginWithAsync(string password)
    {
        RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();
        List<string> response = await raw.CommandAsync("L0", $"LOGIN alice {password}");
        Assert.StartsWith("L0 OK", response[^1]);
        return raw;
    }

    [DbFact]
    public async Task Turning_on_two_factor_authentication_ends_a_session_that_signed_in_with_the_account_password()
    {
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;
        await using RawImapClient raw = await LoginRawAsync("alice");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT INBOX"))[^1]);

        await OpenSessionSupport.TurnOnTwoFactorAsync(Host, Seed.Alice.Id);

        await raw.SendAsync("a2 NOOP\r\n");
        Assert.StartsWith("* BYE", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task A_tenant_that_requires_two_factor_authentication_ends_those_sessions_as_well()
    {
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;
        await using RawImapClient raw = await LoginRawAsync("alice");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT INBOX"))[^1]);

        await OpenSessionSupport.RequireTwoFactorForEveryoneAsync(Host, Seed.Tenant.Id);

        await raw.SendAsync("a2 NOOP\r\n");
        Assert.StartsWith("* BYE", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task A_session_with_an_app_password_goes_on_when_two_factor_authentication_starts_and_ends_when_the_app_password_is_revoked()
    {
        string appPassword = await OpenSessionSupport.CreateAppPasswordAsync(Host, Seed.Alice);
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;
        await using RawImapClient raw = await LoginWithAsync(appPassword);
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT INBOX"))[^1]);

        await OpenSessionSupport.TurnOnTwoFactorAsync(Host, Seed.Alice.Id);
        Assert.StartsWith("a2 OK", (await raw.CommandAsync("a2", "NOOP"))[^1]);

        await OpenSessionSupport.RevokeAppPasswordsAsync(Host, Seed.Alice.Id);
        await raw.SendAsync("a3 NOOP\r\n");
        Assert.StartsWith("* BYE", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }
}

public class OpenSmtpSessionTests : IAsyncLifetime
{
    private const string Password = TestMailClients.Password;

    private TestHost _host = null!;
    private Seed _seed = null!;
    private RunningSmtpServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _server = await RunningSmtpServer.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static string Plain(string login, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{login}\0{password}"));

    private async Task<(RawSmtpClient Client, int Code)> SignInAsync(string password)
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.ImplicitTlsPort, implicitTls: true);
        await client.EhloAsync();
        return (client, (await client.CommandAsync("AUTH PLAIN " + Plain("alice", password))).Code);
    }

    [DbFact]
    public async Task Turning_on_two_factor_authentication_ends_a_session_that_signed_in_with_the_account_password()
    {
        (RawSmtpClient client, int code) = await SignInAsync(Password);
        await using (client)
        {
            Assert.Equal(235, code);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<alice@example.test>")).Code);
            await client.CommandAsync("RSET");

            await OpenSessionSupport.TurnOnTwoFactorAsync(_host, _seed.Alice.Id);

            Assert.Equal("530 5.7.0 Authentication required", (await client.CommandAsync("MAIL FROM:<alice@example.test>")).ToString());
        }
    }

    [DbFact]
    public async Task A_session_with_an_app_password_goes_on_until_it_is_revoked()
    {
        await OpenSessionSupport.TurnOnTwoFactorAsync(_host, _seed.Alice.Id);
        string appPassword = await OpenSessionSupport.CreateAppPasswordAsync(_host, _seed.Alice);

        (RawSmtpClient refused, int refusedCode) = await SignInAsync(Password);
        await using (refused)
        {
            Assert.Equal(535, refusedCode);
        }

        (RawSmtpClient client, int code) = await SignInAsync(appPassword);
        await using (client)
        {
            Assert.Equal(235, code);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<alice@example.test>")).Code);
            await client.CommandAsync("RSET");

            await OpenSessionSupport.RevokeAppPasswordsAsync(_host, _seed.Alice.Id);

            Assert.Equal("530 5.7.0 Authentication required", (await client.CommandAsync("MAIL FROM:<alice@example.test>")).ToString());
        }
    }
}

public class GateAddressTests
{
    [Theory]
    [InlineData("/css/app.css")]
    [InlineData("/js/app.js")]
    [InlineData("/icons/logo.svg")]
    [InlineData("/brand/0f5c1b6e0c8a4a9c8d7e1a2b3c4d5e6f")]
    [InlineData("/favicon.ico")]
    public void The_files_that_pages_are_drawn_with_stay_open(string path) => Assert.True(GatePaths.IsStatic(path));

    [Theory]
    [InlineData("/api/mail/messages/12/cid/logo.png")]
    [InlineData("/api/mail/messages/12/attachments/3/report.pdf")]
    [InlineData("/Mail/x.js")]
    [InlineData("/Admin/Users/Edit")]
    public void The_addresses_of_the_mail_stay_closed_even_when_they_end_in_a_file_name(string path) => Assert.False(GatePaths.IsStatic(path));

    private static async Task<bool> ReachedWhileHeldBackAsync(string path)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AppClaims.MustChangePassword, "1") }, "test"));
        var context = new DefaultHttpContext { User = user };
        context.Request.Path = path;
        bool reached = false;
        await new MustChangePasswordMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);
        return reached;
    }

    [Theory]
    [InlineData("/Account/Password", true)]
    [InlineData("/css/app.css", true)]
    [InlineData("/js/app.js", true)]
    [InlineData("/Account/Logout", true)]
    [InlineData("/api/mail/messages/12/cid/logo.png", false)]
    [InlineData("/Mail", false)]
    [InlineData("/Admin", false)]
    public async Task Somebody_who_has_to_change_the_password_first_reaches_nothing_else(string path, bool open)
        => Assert.Equal(open, await ReachedWhileHeldBackAsync(path));
}

public class EnrolmentPasswordTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DbFact]
    public async Task Setting_up_the_authenticator_asks_for_the_password_first()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        (string? error, string? secret) = await service.StartEnrolmentAsync(_seed.Alice.Id, "not the password", "127.0.0.1");
        Assert.Equal(SignInService.WrongPasswordMessage, error);
        Assert.Null(secret);
        Assert.False(await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().UserTotps.AnyAsync(t => t.UserId == _seed.Alice.Id));

        (error, secret) = await service.StartEnrolmentAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, "127.0.0.1");
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(secret));
    }
}

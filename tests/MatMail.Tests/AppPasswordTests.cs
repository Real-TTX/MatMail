using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

public class AppPasswordFormatTests
{
    [Fact]
    public void A_generated_password_is_long_random_and_without_look_alike_characters()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            string password = AppPasswordService.Generate();
            Assert.Equal(24, password.Length);
            Assert.Matches("^[a-hj-km-np-z2-9]{24}$", password);
            Assert.DoesNotMatch("[ilo01]", password);
            Assert.True(seen.Add(password));
        }
    }

    [Fact]
    public void It_is_shown_in_groups_of_four()
        => Assert.Equal("abcd-efgh-jkmn-pqrs-tuvw-xyz2", AppPasswordService.Format("abcdefghjkmnpqrstuvwxyz2"));

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs-tuvw-xyz2")]
    [InlineData("ABCD-EFGH-JKMN-PQRS-TUVW-XYZ2")]
    [InlineData("abcdefghjkmnpqrstuvwxyz2")]
    [InlineData("  abcd efgh jkmn pqrs tuvw xyz2 ")]
    public void It_is_read_forgivingly(string typed)
        => Assert.Equal("abcdefghjkmnpqrstuvwxyz2", AppPasswordService.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Test-Passw0rd!")]
    [InlineData("abcd-efgh-jkmn-pqrs-tuvw")]
    [InlineData("abcd-efgh-jkmn-pqrs-tuvw-xyz2-abcd")]
    [InlineData("abcd-efgh-jkmn-pqrs-tuvw-xyz0")]
    public void Anything_else_is_an_ordinary_password_and_not_looked_at_as_an_app_password(string? typed)
        => Assert.Null(AppPasswordService.Normalize(typed));
}

/// <summary>App passwords and what the mail protocols accept: the password, an app password, or only an app password.</summary>
public class AppPasswordTests : IAsyncLifetime
{
    private readonly TestClock _clock = new();
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddSingleton<TimeProvider>(_clock));
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailUser?> ProtocolLoginAsync(string login, string secret, string ip = "127.0.0.1")
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync(login, secret, ip);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Creating, listing, revoking
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task An_app_password_is_shown_once_and_stored_as_a_hash()
    {
        string first = await _host.CreateAppPasswordAsync(_seed.Alice, "Thunderbird laptop");
        string second = await _host.CreateAppPasswordAsync(_seed.Alice, "Phone");

        Assert.Matches("^([a-hj-km-np-z2-9]{4}-){5}[a-hj-km-np-z2-9]{4}$", first);
        Assert.NotEqual(first, second);
        string raw = first.Replace("-", string.Empty);
        Assert.True(raw.Length >= 20);

        List<AppPassword> rows = await _host.ReadAsync(db => db.AppPasswords.AsNoTracking().Where(a => a.UserId == _seed.Alice.Id).OrderBy(a => a.CreateDate).ToListAsync());
        Assert.Equal(new[] { "Thunderbird laptop", "Phone" }, rows.Select(r => r.Name));
        Assert.Equal(raw[..4], rows[0].Prefix);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain(raw[4..], row.SecretHash);
            Assert.NotEqual(Guid.Empty, row.Token);
            Assert.Null(row.LastUsedDate);
        });
        Assert.NotEqual(rows[0].Token, rows[1].Token);
    }

    [DbFact]
    public async Task Creating_one_needs_the_password_again()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        Assert.Equal("The password is wrong.", (await service.CreateAppPasswordAsync(_seed.Alice.Id, "Phone", "nope", "127.0.0.1")).Error);
        Assert.Equal("The password is wrong.", (await service.CreateAppPasswordAsync(_seed.Alice.Id, "Phone", null, "127.0.0.1")).Error);

        Assert.Empty(await service.ListAppPasswordsAsync(_seed.Alice.Id));
        Assert.Equal(2, (await _host.ReloadAsync(_seed.Alice)).FailedLoginCount);
    }

    [DbFact]
    public async Task The_name_is_required_and_at_most_a_hundred_characters()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        Assert.Equal("Enter a name for the app password.", (await service.CreateAppPasswordAsync(_seed.Alice.Id, "   ", TwoFactorTestExtensions.Password, null)).Error);
        Assert.Equal("The name must not be longer than 100 characters.", (await service.CreateAppPasswordAsync(_seed.Alice.Id, new string('x', 101), TwoFactorTestExtensions.Password, null)).Error);
        Assert.Null((await service.CreateAppPasswordAsync(_seed.Alice.Id, "  Outlook  ", TwoFactorTestExtensions.Password, null)).Error);
        Assert.Equal("Outlook", (await service.ListAppPasswordsAsync(_seed.Alice.Id)).Single().Name);
    }

    [DbFact]
    public async Task A_user_can_have_twenty_app_passwords()
    {
        for (int i = 0; i < AppPasswordService.MaxPerUser; i++)
        {
            await _host.CreateAppPasswordAsync(_seed.Alice, "Device " + i);
        }

        using IServiceScope scope = _host.Scope();
        (string? error, string? password) = await scope.ServiceProvider.GetRequiredService<TwoFactorService>()
            .CreateAppPasswordAsync(_seed.Alice.Id, "One too many", TwoFactorTestExtensions.Password, null);

        Assert.Equal("You can have at most 20 app passwords. Revoke one you no longer need.", error);
        Assert.Null(password);
    }

    [DbFact]
    public async Task A_revoked_app_password_stops_working_and_only_the_owner_can_revoke_it()
    {
        string password = await _host.CreateAppPasswordAsync(_seed.Alice, "Old phone");
        Assert.NotNull(await ProtocolLoginAsync("alice", password));

        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        AppPassword row = (await service.ListAppPasswordsAsync(_seed.Alice.Id)).Single();

        Assert.Equal("The app password does not exist.", await service.RevokeAppPasswordAsync(_seed.Bob.Id, row.Token));
        Assert.Single(await service.ListAppPasswordsAsync(_seed.Alice.Id));

        Assert.Null(await service.RevokeAppPasswordAsync(_seed.Alice.Id, row.Token));
        Assert.Empty(await service.ListAppPasswordsAsync(_seed.Alice.Id));
        Assert.Null(await ProtocolLoginAsync("alice", password));
    }

    [DbFact]
    public async Task Using_an_app_password_notes_the_day_and_the_address()
    {
        string password = await _host.CreateAppPasswordAsync(_seed.Alice);
        Assert.NotNull(await ProtocolLoginAsync("alice", password, "203.0.113.7"));

        AppPassword row = await _host.ReadAsync(db => db.AppPasswords.AsNoTracking().SingleAsync(a => a.UserId == _seed.Alice.Id));
        Assert.NotNull(row.LastUsedDate);
        Assert.True(DateTime.UtcNow - row.LastUsedDate!.Value < TimeSpan.FromMinutes(1));
        Assert.Equal("203.0.113.7", row.LastUsedIp);

        Assert.NotNull(await ProtocolLoginAsync("alice", password, "198.51.100.9"));
        Assert.Equal("198.51.100.9", (await _host.ReadAsync(db => db.AppPasswords.AsNoTracking().SingleAsync(a => a.UserId == _seed.Alice.Id))).LastUsedIp);
    }

    [DbFact]
    public async Task Deleting_a_user_deletes_their_app_passwords()
    {
        await _host.CreateAppPasswordAsync(_seed.Bob);
        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<UserService>().DeleteAsync(_seed.Bob.Id, deleteMailbox: false));
        }

        Assert.Equal(0, await _host.ReadAsync(db => db.AppPasswords.CountAsync(a => a.UserId == _seed.Bob.Id)));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // What each sign-in accepts
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Without_two_factor_both_the_password_and_app_passwords_open_the_mail_protocols()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);

        Assert.NotNull(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
        Assert.NotNull(await ProtocolLoginAsync("alice", appPassword));
        Assert.NotNull(await ProtocolLoginAsync("alice@example.test", appPassword));
        Assert.NotNull(await ProtocolLoginAsync("ALICE", appPassword.ToUpperInvariant().Replace("-", string.Empty)));
    }

    [DbFact]
    public async Task An_app_password_never_signs_in_on_the_web()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);

        SignInOutcome web = await _host.SignInAsync("alice", appPassword, SignInPurpose.Web);

        Assert.Equal(SignInStatus.InvalidCredentials, web.Status);
        Assert.Null(web.User);
        Assert.True((await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web)).Succeeded);
    }

    [DbFact]
    public async Task With_two_factor_only_app_passwords_open_the_mail_protocols()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);
        await _host.EnrolAsync(_clock, _seed.Alice);

        Assert.Null(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
        Assert.NotNull(await ProtocolLoginAsync("alice", appPassword));

        SignInOutcome refused = await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Protocol);
        Assert.Equal(SignInStatus.InvalidCredentials, refused.Status);

        // The web still takes the password (and then asks for the code), and never the app password.
        SignInOutcome web = await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web);
        Assert.True(web.Succeeded);
        Assert.True(web.SecondFactorPending);
        Assert.False((await _host.SignInAsync("alice", appPassword, SignInPurpose.Web)).Succeeded);
    }

    [DbFact]
    public async Task Turning_two_factor_off_gives_the_password_back_to_the_mail_protocols()
    {
        await _host.EnrolAsync(_clock, _seed.Alice);
        Assert.Null(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));

        using (IServiceScope admin = _host.ScopeAs(_seed.Bob, Permissions.UsersManage))
        {
            Assert.Null(await admin.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Alice.Id));
        }

        Assert.NotNull(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
    }

    [DbFact]
    public async Task Whoever_must_use_two_factor_is_treated_like_someone_who_does_even_before_setting_it_up()
    {
        string earlier = await _host.CreateAppPasswordAsync(_seed.Alice, "Created before the rule");
        await _host.SetPolicyAsync(_seed.Tenant.Id, TwoFactorMode.Everyone);

        // The mail programs need an app password now ...
        Assert.Null(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
        Assert.NotNull(await ProtocolLoginAsync("alice", earlier));

        // ... while the web takes the password and leads to the set-up (there is no code yet to ask for).
        SignInOutcome web = await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web);
        Assert.True(web.Succeeded);
        Assert.False(web.SecondFactorPending);
        Assert.True(web.TwoFactor!.SetupRequired);

        // A new app password is only handed out once the second factor is set up: it would be a way around the rule.
        using IServiceScope scope = _host.Scope();
        (string? error, string? created) = await scope.ServiceProvider.GetRequiredService<TwoFactorService>()
            .CreateAppPasswordAsync(_seed.Alice.Id, "New phone", TwoFactorTestExtensions.Password, null);
        Assert.Equal("Set up two-factor authentication first.", error);
        Assert.Null(created);

        await _host.EnrolAsync(_clock, _seed.Alice);
        Assert.Null((await scope.ServiceProvider.GetRequiredService<TwoFactorService>().CreateAppPasswordAsync(_seed.Alice.Id, "New phone", TwoFactorTestExtensions.Password, null)).Error);
    }

    [DbFact]
    public async Task A_role_that_requires_two_factor_closes_the_password_for_the_mail_protocols_too()
    {
        Role strict = await _host.CreateRoleAsync(_seed.Tenant.Id, "Finance", requiresTwoFactor: true, Permissions.MailUse);
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.UserRoles.Add(new UserRole { UserId = _seed.Bob.Id, RoleId = strict.Id });
            await db.SaveChangesAsync();
        }

        Assert.Null(await ProtocolLoginAsync("bob", TwoFactorTestExtensions.Password));
        Assert.NotNull(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
    }

    [DbFact]
    public async Task The_app_password_of_somebody_else_does_not_work()
    {
        string alices = await _host.CreateAppPasswordAsync(_seed.Alice);

        Assert.Null(await ProtocolLoginAsync("bob", alices));
        Assert.NotNull(await ProtocolLoginAsync("alice", alices));
    }

    [DbFact]
    public async Task An_app_password_still_needs_the_permission_to_use_mail_and_an_active_user()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);
        await ExecuteAsync("UPDATE \"User\" SET \"IsActive\" = false WHERE \"Id\" = " + _seed.Alice.Id);

        Assert.Null(await ProtocolLoginAsync("alice", appPassword));
        Assert.Equal(SignInStatus.Disabled, (await _host.SignInAsync("alice", appPassword, SignInPurpose.Protocol)).Status);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Failures
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Wrong_app_passwords_count_like_wrong_passwords_and_lock_the_account()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);
        string sameStart = appPassword[..4] + "-" + AppPasswordService.Format(AppPasswordService.Generate())[5..];

        for (int i = 0; i < 5; i++)
        {
            // Half the guesses start like the real one (they meet a stored prefix), half do not.
            string guess = i % 2 == 0 ? sameStart : AppPasswordService.Format(AppPasswordService.Generate());
            Assert.Null(await ProtocolLoginAsync("alice", guess));
        }

        User locked = await _host.ReloadAsync(_seed.Alice);
        Assert.NotNull(locked.LockedUntilDate);

        Assert.Equal(SignInStatus.LockedOut, (await _host.SignInAsync("alice", appPassword, SignInPurpose.Protocol)).Status);
        Assert.Null(await ProtocolLoginAsync("alice", appPassword));
    }

    [DbFact]
    public async Task The_account_password_refused_for_the_mail_protocols_counts_as_a_failure()
    {
        await _host.EnrolAsync(_clock, _seed.Alice);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(await ProtocolLoginAsync("alice", TwoFactorTestExtensions.Password));
        }

        Assert.NotNull((await _host.ReloadAsync(_seed.Alice)).LockedUntilDate);
        string message = await _host.ReadAsync(db => db.ActivityLogs.AsNoTracking().Where(l => l.Message.Contains("app password only")).Select(l => l.Message).FirstAsync());
        Assert.Contains("'alice'", message);
        Assert.DoesNotContain(TwoFactorTestExtensions.Password, message);
    }

    [DbFact]
    public async Task A_correct_app_password_takes_back_the_failures()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(await ProtocolLoginAsync("alice", AppPasswordService.Format(AppPasswordService.Generate())));
        }

        Assert.Equal(3, (await _host.ReloadAsync(_seed.Alice)).FailedLoginCount);
        Assert.NotNull(await ProtocolLoginAsync("alice", appPassword));
        Assert.Equal(0, (await _host.ReloadAsync(_seed.Alice)).FailedLoginCount);
    }

    private async Task ExecuteAsync(string sql)
    {
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.ExecuteSqlRawAsync(sql);
    }
}

/// <summary>The real IMAP and SMTP servers: with two-factor authentication on, only an app password gets in.</summary>
public class TwoFactorProtocolServerTests : IAsyncLifetime
{
    private readonly TestClock _clock = new();
    private TestHost _host = null!;
    private Seed _seed = null!;
    private ImapTestServer _imap = null!;
    private RunningSmtpServer _smtp = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddSingleton<TimeProvider>(_clock));
        _seed = await _host.SeedAsync();
        _imap = await ImapTestServer.StartAsync(_host);
        _smtp = await RunningSmtpServer.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _smtp.DisposeAsync();
        await _imap.DisposeAsync();
        await _host.DisposeAsync();
    }

    [DbFact]
    public async Task Imap_takes_the_app_password_and_refuses_the_account_password_while_two_factor_is_on()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice, "Thunderbird");

        // Before two-factor authentication both work.
        using (MailKit.Net.Imap.ImapClient before = await _imap.ConnectAsync())
        {
            await before.AuthenticateAsync("alice", ImapTestServer.Password);
            Assert.True(before.IsAuthenticated);
        }

        await _host.EnrolAsync(_clock, _seed.Alice);

        using (MailKit.Net.Imap.ImapClient refused = await _imap.ConnectAsync())
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => refused.AuthenticateAsync("alice", ImapTestServer.Password));
        }

        using (MailKit.Net.Imap.ImapClient accepted = await _imap.ConnectAsync())
        {
            await accepted.AuthenticateAsync("alice", appPassword);
            Assert.True(accepted.IsAuthenticated);
        }
    }

    [DbFact]
    public async Task Smtp_takes_the_app_password_and_refuses_the_account_password_while_two_factor_is_on()
    {
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice, "Outlook");
        await _host.EnrolAsync(_clock, _seed.Alice);

        using (SmtpClient refused = await TestMailClients.ConnectAsync(_smtp.SubmissionPort, SecureSocketOptions.StartTls))
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => refused.AuthenticateAsync(new SaslMechanismPlain("alice", TestMailClients.Password)));
        }

        using (SmtpClient accepted = await TestMailClients.ConnectAsync(_smtp.SubmissionPort, SecureSocketOptions.StartTls))
        {
            await accepted.AuthenticateAsync(new SaslMechanismLogin("alice", appPassword));
            Assert.True(accepted.IsAuthenticated);
        }
    }
}

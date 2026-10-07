using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>Setting up the authenticator, the second step of the sign-in, recovery codes, turning it off, the reset by an administrator.</summary>
public class TwoFactorTests : IAsyncLifetime
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

    // -------------------------------------------------------------------------------------------------------------------
    // Enrolment
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_authenticator_only_counts_once_a_right_code_confirmed_it()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        (string? error, string? secret) = await service.BeginEnrolmentAsync(_seed.Alice.Id);
        Assert.Null(error);
        Assert.Equal(32, secret!.Length);
        Assert.False((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);
        Assert.Equal(secret, (await service.GetOverviewAsync(_seed.Alice.Id)).PendingSecret);

        Assert.True(Base32.TryDecode(secret, out byte[] key));
        string wrong = TwoFactorTestExtensions.NotACode(key, _clock);
        (error, IReadOnlyList<string> codes) = await service.ConfirmEnrolmentAsync(_seed.Alice.Id, wrong, null, "127.0.0.1");
        Assert.NotNull(error);
        Assert.Empty(codes);
        Assert.False((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);

        (error, codes) = await service.ConfirmEnrolmentAsync(_seed.Alice.Id, Totp.Compute(key, _clock.Step), null, "127.0.0.1");
        Assert.Null(error);
        Assert.Equal(TwoFactorService.RecoveryCodeCount, codes.Count);
        TwoFactorOverview overview = await service.GetOverviewAsync(_seed.Alice.Id);
        Assert.True(overview.Status.Enabled);
        Assert.NotNull(overview.EnabledSince);
        Assert.Null(overview.PendingSecret);
        Assert.Equal(TwoFactorService.RecoveryCodeCount, overview.RecoveryCodesLeft);
    }

    [DbFact]
    public async Task The_secret_is_stored_encrypted_and_the_recovery_codes_only_as_hashes()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        UserTotp totp = await _host.ReadAsync(db => db.UserTotps.AsNoTracking().SingleAsync(t => t.UserId == _seed.Alice.Id));
        string plainSecret = Base32.Encode(alice.Key);
        Assert.DoesNotContain(plainSecret, totp.Secret, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(totp.ConfirmedDate);

        List<UserRecoveryCode> stored = await _host.ReadAsync(db => db.UserRecoveryCodes.AsNoTracking().Where(c => c.UserId == _seed.Alice.Id).ToListAsync());
        Assert.Equal(TwoFactorService.RecoveryCodeCount, stored.Count);
        Assert.All(alice.RecoveryCodes, code =>
        {
            Assert.Matches("^[a-hj-km-np-z2-9]{5}-[a-hj-km-np-z2-9]{5}$", code);
            Assert.DoesNotContain(stored, row => row.CodeHash.Contains(code.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(TwoFactorService.RecoveryCodeCount, alice.RecoveryCodes.Distinct().Count());
        Assert.All(stored, row => Assert.Null(row.UsedDate));
    }

    [DbFact]
    public async Task A_new_set_up_replaces_one_that_was_started_and_a_running_one_cannot_be_started_again()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        (_, string? first) = await service.BeginEnrolmentAsync(_seed.Bob.Id);
        (_, string? second) = await service.BeginEnrolmentAsync(_seed.Bob.Id);
        Assert.NotEqual(first, second);
        Assert.Equal(second, (await service.GetOverviewAsync(_seed.Bob.Id)).PendingSecret);

        await service.CancelEnrolmentAsync(_seed.Bob.Id);
        Assert.Null((await service.GetOverviewAsync(_seed.Bob.Id)).PendingSecret);

        await _host.EnrolAsync(_clock, _seed.Bob);
        (string? error, string? secret) = await service.BeginEnrolmentAsync(_seed.Bob.Id);
        Assert.Equal("Two-factor authentication is already on.", error);
        Assert.Null(secret);
        Assert.Null((await service.GetOverviewAsync(_seed.Bob.Id)).PendingSecret);
    }

    [DbFact]
    public async Task Turning_it_on_ends_the_other_sessions_but_not_the_current_one()
    {
        Guid current = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            foreach (Guid token in new[] { current, other })
            {
                db.UserSessions.Add(new UserSession { Token = token, UserId = _seed.Alice.Id, TenantId = _seed.Tenant.Id, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow });
            }

            await db.SaveChangesAsync();
        }

        await _host.EnrolAsync(_clock, _seed.Alice, keepSession: current);

        Guid[] left = await _host.ReadAsync(db => db.UserSessions.Where(s => s.UserId == _seed.Alice.Id).Select(s => s.Token).ToArrayAsync());
        Assert.Equal(new[] { current }, left);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The second step of the sign-in
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_password_step_waits_for_a_code_when_the_authenticator_is_on()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        SignInOutcome web = await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web);
        Assert.True(web.Succeeded);
        Assert.True(web.SecondFactorPending);
        Assert.True(web.TwoFactor!.Enabled);

        SecondStepOutcome second = await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock));
        Assert.True(second.Result.Accepted);
        Assert.False(second.Result.UsedRecoveryCode);
        Assert.Equal(_seed.Alice.Id, second.User!.Id);
    }

    [DbFact]
    public async Task A_user_without_the_authenticator_signs_in_with_the_password_alone()
    {
        SignInOutcome web = await _host.SignInAsync("bob", TwoFactorTestExtensions.Password, SignInPurpose.Web);
        Assert.True(web.Succeeded);
        Assert.False(web.SecondFactorPending);
        Assert.False(web.TwoFactor!.Enabled);
        Assert.False(web.TwoFactor.Required);
    }

    [DbFact]
    public async Task A_code_is_accepted_only_once()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        string code = alice.CodeNow(_clock);

        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, code)).Result.Accepted);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, code)).Result.Status);

        // Not even from a phone whose clock is a step behind: the steps up to the one used are spent.
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock, -1))).Result.Status);

        // The next code works.
        _clock.NextStep();
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock))).Result.Accepted);
    }

    [DbFact]
    public async Task The_code_used_to_confirm_the_set_up_cannot_be_used_again_to_sign_in()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        (_, string? secret) = await service.BeginEnrolmentAsync(_seed.Alice.Id);
        Base32.TryDecode(secret, out byte[] key);
        string code = Totp.Compute(key, _clock.Step);
        Assert.Null((await service.ConfirmEnrolmentAsync(_seed.Alice.Id, code, null, null)).Error);

        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, code)).Result.Status);
    }

    [DbFact]
    public async Task The_clock_of_the_phone_may_be_one_step_off_but_not_two()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        _clock.AdvanceSteps(10);

        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock, -2))).Result.Status);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock, 2))).Result.Status);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock, -1))).Result.Accepted);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock, 1))).Result.Accepted);

        // Steps up to the latest one accepted are spent.
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock))).Result.Status);
    }

    [DbFact]
    public async Task A_code_may_be_typed_with_a_space_and_anything_else_is_refused()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        string code = alice.CodeNow(_clock);

        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, "12345")).Result.Status);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, null)).Result.Status);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, "")).Result.Status);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, code[..3] + " " + code[3..])).Result.Accepted);
    }

    [DbFact]
    public async Task Wrong_codes_lock_the_account_like_wrong_passwords()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.WrongCode(_clock, i))).Result.Status);
        }

        User locked = await _host.ReloadAsync(_seed.Alice);
        Assert.NotNull(locked.LockedUntilDate);
        Assert.True(locked.LockedUntilDate > DateTime.UtcNow.AddMinutes(4));

        // Neither the right code nor the right password gets through while the account is locked.
        Assert.Equal(SecondFactorStatus.LockedOut, (await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock))).Result.Status);
        Assert.Equal(SignInStatus.LockedOut, (await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web)).Status);

        // After the five minutes the right code works again.
        await ExecuteAsync("UPDATE \"User\" SET \"LockedUntilDate\" = now() - interval '1 minute' WHERE \"Id\" = " + _seed.Alice.Id);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock))).Result.Accepted);
    }

    [DbFact]
    public async Task Passing_the_password_step_again_does_not_wipe_out_the_wrong_codes()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        // Guessing codes in rounds of four, each round after a fresh (right) password, must not stay below the limit for ever.
        for (int i = 0; i < 4; i++)
        {
            await _host.VerifyLoginAsync(_seed.Alice, alice.WrongCode(_clock, i));
        }

        SignInOutcome again = await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web);
        Assert.True(again.SecondFactorPending);
        Assert.Equal(4, (await _host.ReloadAsync(_seed.Alice)).FailedLoginCount);

        await _host.VerifyLoginAsync(_seed.Alice, alice.WrongCode(_clock, 4));
        Assert.NotNull((await _host.ReloadAsync(_seed.Alice)).LockedUntilDate);

        // A completed sign-in (password and code) starts from zero again.
        await ExecuteAsync("UPDATE \"User\" SET \"LockedUntilDate\" = NULL, \"FailedLoginCount\" = 3 WHERE \"Id\" = " + _seed.Alice.Id);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock))).Result.Accepted);
        User done = await _host.ReloadAsync(_seed.Alice);
        Assert.Equal(0, done.FailedLoginCount);
        Assert.NotNull(done.LastLoginDate);
    }

    [DbFact]
    public async Task Parallel_wrong_codes_are_all_counted()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => _host.VerifyLoginAsync(_seed.Alice, alice.WrongCode(_clock, i))));

        User user = await _host.ReloadAsync(_seed.Alice);
        Assert.Equal(4, user.FailedLoginCount);
        Assert.Null(user.LockedUntilDate);
    }

    [DbFact]
    public async Task The_same_code_sent_twice_at_once_is_accepted_once()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        string code = alice.CodeNow(_clock);

        SecondStepOutcome[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _host.VerifyLoginAsync(_seed.Alice, code)));

        Assert.Equal(1, results.Count(r => r.Result.Accepted));
    }

    [DbFact]
    public async Task A_user_who_was_disabled_meanwhile_does_not_get_in()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        await ExecuteAsync("UPDATE \"User\" SET \"IsActive\" = false WHERE \"Id\" = " + _seed.Alice.Id);

        SecondStepOutcome outcome = await _host.VerifyLoginAsync(_seed.Alice, alice.CodeNow(_clock));

        Assert.Null(outcome.User);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Recovery codes
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_recovery_code_works_once()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        string code = alice.RecoveryCodes[0];

        SecondStepOutcome first = await _host.VerifyLoginAsync(_seed.Alice, code);
        Assert.True(first.Result.Accepted);
        Assert.True(first.Result.UsedRecoveryCode);
        Assert.Equal(9, first.Result.RecoveryCodesLeft);

        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, code)).Result.Status);

        using IServiceScope scope = _host.Scope();
        Assert.Equal(9, (await scope.ServiceProvider.GetRequiredService<TwoFactorService>().GetOverviewAsync(_seed.Alice.Id)).RecoveryCodesLeft);
    }

    [DbFact]
    public async Task A_recovery_code_may_be_typed_in_capitals_and_without_the_hyphen()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[1].ToUpperInvariant())).Result.Accepted);
        Assert.True((await _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[2].Replace("-", string.Empty))).Result.Accepted);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, "abcde-fghij")).Result.Status);
    }

    [DbFact]
    public async Task The_same_recovery_code_sent_twice_at_once_is_accepted_once()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        SecondStepOutcome[] results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[0])));

        Assert.Equal(1, results.Count(r => r.Result.Accepted));
    }

    [DbFact]
    public async Task New_recovery_codes_replace_the_old_ones_and_need_the_password_and_a_code()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);

        using (IServiceScope scope = _host.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            Assert.Equal("The password is wrong.", (await service.RegenerateRecoveryCodesAsync(_seed.Alice.Id, "nope", alice.CodeNow(_clock), null)).Error);
            Assert.Equal(TwoFactorService.WrongCodeMessage, (await service.RegenerateRecoveryCodesAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.WrongCode(_clock), null)).Error);
        }

        // Still the old ones.
        using (IServiceScope scope = _host.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            (string? error, IReadOnlyList<string> fresh) = await service.RegenerateRecoveryCodesAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.CodeNow(_clock), null);
            Assert.Null(error);
            Assert.Equal(TwoFactorService.RecoveryCodeCount, fresh.Count);
            Assert.Empty(fresh.Intersect(alice.RecoveryCodes));

            Assert.Equal(TwoFactorService.RecoveryCodeCount, (await service.GetOverviewAsync(_seed.Alice.Id)).RecoveryCodesLeft);
            Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[0])).Result.Status);
            Assert.True((await _host.VerifyLoginAsync(_seed.Alice, fresh[0])).Result.Accepted);
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Turning it off
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        Assert.Equal("The password is wrong.", await service.DisableAsync(_seed.Alice.Id, "nope", alice.CodeNow(_clock), null));
        Assert.Equal(TwoFactorService.WrongCodeMessage, await service.DisableAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.WrongCode(_clock), null));
        Assert.True((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);

        Assert.Null(await service.DisableAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.CodeNow(_clock), null));
        Assert.False((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);
        Assert.Equal(0, await _host.ReadAsync(db => db.UserTotps.CountAsync(t => t.UserId == _seed.Alice.Id)));
        Assert.Equal(0, await _host.ReadAsync(db => db.UserRecoveryCodes.CountAsync(c => c.UserId == _seed.Alice.Id)));

        // Signing in is a one-step affair again.
        Assert.False((await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web)).SecondFactorPending);
    }

    [DbFact]
    public async Task A_recovery_code_does_for_turning_it_off_when_the_phone_is_gone()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        Assert.Null(await service.DisableAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.RecoveryCodes[0], null));

        Assert.False((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);
    }

    [DbFact]
    public async Task Wrong_passwords_while_turning_it_off_count_against_the_lockout()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("The password is wrong.", await service.DisableAsync(_seed.Alice.Id, "nope" + i, alice.CodeNow(_clock), null));
        }

        Assert.Equal(SignInService.LockedMessage, await service.DisableAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.CodeNow(_clock), null));
        Assert.True((await service.GetStatusAsync(_seed.Alice.Id)).Enabled);
    }

    [DbFact]
    public async Task Turning_it_off_when_it_is_not_on_is_refused()
    {
        using IServiceScope scope = _host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        Assert.Equal("Two-factor authentication is not on.", await service.DisableAsync(_seed.Bob.Id, TwoFactorTestExtensions.Password, "123456", null));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The reset by an administrator
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task An_administrator_resets_the_authenticator_of_a_user_who_lost_the_phone()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice);
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.UserSessions.Add(new UserSession { Token = Guid.NewGuid(), UserId = _seed.Alice.Id, TenantId = _seed.Tenant.Id, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using (IServiceScope admin = _host.ScopeAs(_seed.Bob, Permissions.UsersManage, Permissions.MailUse))
        {
            Assert.Null(await admin.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Alice.Id));
        }

        Assert.Equal(0, await _host.ReadAsync(db => db.UserTotps.CountAsync(t => t.UserId == _seed.Alice.Id)));
        Assert.Equal(0, await _host.ReadAsync(db => db.UserRecoveryCodes.CountAsync(c => c.UserId == _seed.Alice.Id)));
        Assert.Equal(0, await _host.ReadAsync(db => db.UserSessions.CountAsync(s => s.UserId == _seed.Alice.Id)));
        Assert.Equal(1, await _host.ReadAsync(db => db.AppPasswords.CountAsync(a => a.UserId == _seed.Alice.Id)));

        // The password is enough again, the app password still works for mail programs, the old codes are gone.
        Assert.False((await _host.SignInAsync("alice", TwoFactorTestExtensions.Password, SignInPurpose.Web)).SecondFactorPending);
        Assert.True((await _host.SignInAsync("alice", appPassword, SignInPurpose.Protocol)).Succeeded);
        Assert.Equal(SecondFactorStatus.Wrong, (await _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[0])).Result.Status);

        ActivityLog entry = await _host.ReadAsync(db => db.ActivityLogs.AsNoTracking().Where(l => l.Message.Contains("was reset")).SingleAsync());
        Assert.Equal(ActivityCategory.Admin, entry.Category);
        Assert.Contains("'alice'", entry.Message);
    }

    [DbFact]
    public async Task Only_someone_who_manages_users_may_reset_and_only_in_their_own_tenant()
    {
        await _host.EnrolAsync(_clock, _seed.Alice);

        using (IServiceScope plain = _host.ScopeAs(_seed.Bob, Permissions.MailUse))
        {
            Assert.Equal("You are not allowed to do this.", await plain.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Alice.Id));
        }

        using (IServiceScope self = _host.ScopeAs(_seed.Alice, Permissions.UsersManage))
        {
            Assert.StartsWith("You cannot reset your own", await self.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Alice.Id));
        }

        // A manager of another tenant does not even see her.
        long otherTenant;
        using (IServiceScope scope = _host.Scope())
        {
            (Tenant? tenant, _) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Other", null);
            otherTenant = tenant!.Id;
        }

        User stranger = await _host.CreateUserAsync(otherTenant, "stranger");
        using (IServiceScope outsider = _host.ScopeAs(stranger, Permissions.UsersManage))
        {
            Assert.Equal("The user does not exist.", await outsider.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Alice.Id));
        }

        Assert.True((await StatusAsync(_seed.Alice)).Enabled);
    }

    [DbFact]
    public async Task Only_a_system_administrator_resets_a_system_administrator()
    {
        User root = await _host.CreateUserAsync(_seed.Tenant.Id, "root", isSystemAdmin: true);
        await _host.EnrolAsync(_clock, root);

        using (IServiceScope manager = _host.ScopeAs(_seed.Alice, Permissions.UsersManage))
        {
            Assert.Equal(
                "Only system administrators can change a system administrator.",
                await manager.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(root.Id));
        }

        Assert.True((await StatusAsync(root)).Enabled);

        User second = await _host.CreateUserAsync(_seed.Tenant.Id, "second", isSystemAdmin: true);
        using (IServiceScope sysadmin = _host.Scope(c => c.RunAs(second.Id, _seed.Tenant.Id, isSystemAdmin: true, new[] { Permissions.UsersManage }, "Second")))
        {
            Assert.Null(await sysadmin.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(root.Id));
        }

        Assert.False((await StatusAsync(root)).Enabled);
    }

    [DbFact]
    public async Task A_reset_of_somebody_without_the_authenticator_says_so()
    {
        using IServiceScope manager = _host.ScopeAs(_seed.Alice, Permissions.UsersManage);

        Assert.Equal("Two-factor authentication is not on for this user.", await manager.ServiceProvider.GetRequiredService<TwoFactorService>().ResetAsync(_seed.Bob.Id));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The activity log
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_activity_log_tells_what_happened_but_never_a_secret()
    {
        EnrolledUser alice = await _host.EnrolAsync(_clock, _seed.Alice);
        await _host.VerifyLoginAsync(_seed.Alice, alice.WrongCode(_clock));
        await _host.VerifyLoginAsync(_seed.Alice, alice.RecoveryCodes[0]);
        string appPassword = await _host.CreateAppPasswordAsync(_seed.Alice, "Phone");
        using (IServiceScope scope = _host.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            AppPassword row = (await service.ListAppPasswordsAsync(_seed.Alice.Id)).Single();
            Assert.Null(await service.RevokeAppPasswordAsync(_seed.Alice.Id, row.Token));
            Assert.Null(await service.DisableAsync(_seed.Alice.Id, TwoFactorTestExtensions.Password, alice.RecoveryCodes[1], null));
        }

        List<string> messages = await _host.ReadAsync(db => db.ActivityLogs.AsNoTracking().Select(l => l.Message + " " + l.Details).ToListAsync());
        Assert.Contains(messages, m => m.Contains("turned on two-factor authentication"));
        Assert.Contains(messages, m => m.Contains("wrong second factor"));
        Assert.Contains(messages, m => m.Contains("used a recovery code"));
        Assert.Contains(messages, m => m.Contains("created the app password 'Phone'"));
        Assert.Contains(messages, m => m.Contains("revoked the app password 'Phone'"));
        Assert.Contains(messages, m => m.Contains("turned off two-factor authentication"));

        string everything = string.Join('\n', messages);
        Assert.DoesNotContain(Base32.Encode(alice.Key), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TwoFactorTestExtensions.Password, everything);
        Assert.DoesNotContain(appPassword.Replace("-", string.Empty), everything.Replace("-", string.Empty));
        foreach (string code in alice.RecoveryCodes)
        {
            Assert.DoesNotContain(code, everything);
        }
    }

    private async Task<TwoFactorStatus> StatusAsync(User user)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<TwoFactorService>().GetStatusAsync(user.Id);
    }

    private async Task ExecuteAsync(string sql)
    {
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.ExecuteSqlRawAsync(sql);
    }
}

public class RecoveryCodeFormatTests
{
    [Fact]
    public void Recovery_codes_look_like_two_groups_of_five_without_look_alike_characters()
    {
        for (int i = 0; i < 100; i++)
        {
            string code = TwoFactorService.GenerateRecoveryCode();
            Assert.Matches("^[a-hj-km-np-z2-9]{5}-[a-hj-km-np-z2-9]{5}$", code);
            Assert.DoesNotMatch("[ilo01]", code);
            Assert.Equal(code.Replace("-", string.Empty), TwoFactorService.NormalizeRecoveryCode(code));
        }
    }

    [Theory]
    [InlineData("ABCDE-FGHJK", "abcdefghjk")]
    [InlineData("abcde fghjk", "abcdefghjk")]
    [InlineData("abcdefghjk", "abcdefghjk")]
    public void Recovery_codes_are_read_forgivingly(string typed, string expected)
        => Assert.Equal(expected, TwoFactorService.NormalizeRecoveryCode(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcde-fghj")]
    [InlineData("abcde-fghjkm")]
    [InlineData("abcde-fghi0")]
    [InlineData("123456")]
    public void Anything_else_is_not_a_recovery_code(string? typed)
        => Assert.Null(TwoFactorService.NormalizeRecoveryCode(typed));
}

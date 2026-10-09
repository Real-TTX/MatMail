using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests.Support;

/// <summary>A clock the tests wind forward: authenticator codes change every 30 seconds.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public long Step => Totp.StepOf(_now);

    public void Advance(TimeSpan by) => _now += by;

    /// <summary>Moves on to the start of the next 30 second step.</summary>
    public void NextStep() => _now = DateTimeOffset.FromUnixTimeSeconds((Step + 1) * Totp.PeriodSeconds);

    public void AdvanceSteps(int steps) => _now = DateTimeOffset.FromUnixTimeSeconds((Step + steps) * Totp.PeriodSeconds);
}

/// <summary>A user who turned two-factor authentication on: the key of their authenticator app and the recovery codes.</summary>
public sealed record EnrolledUser(User User, byte[] Key, IReadOnlyList<string> RecoveryCodes);

public static class TwoFactorTestExtensions
{
    public const string Password = "Test-Passw0rd!";

    /// <summary>The code the authenticator app of the user shows at the moment (or <paramref name="stepOffset"/> steps away from it).</summary>
    public static string CodeNow(this EnrolledUser enrolled, TestClock clock, int stepOffset = 0)
        => Totp.Compute(enrolled.Key, clock.Step + stepOffset);

    /// <summary>A six-digit code that is wrong now, whatever the clock of the phone: not one of the codes of the steps around the current one.</summary>
    public static string WrongCode(this EnrolledUser enrolled, TestClock clock, int n = 0) => NotACode(enrolled.Key, clock, n);

    public static string NotACode(byte[] key, TestClock clock, int n = 0)
    {
        var valid = Enumerable.Range(-3, 7).Select(offset => Totp.Compute(key, clock.Step + offset)).ToHashSet();
        return Enumerable.Range(0, 1000).Select(i => i.ToString("D6")).Where(code => !valid.Contains(code)).ElementAt(n);
    }

    /// <summary>Sets up and confirms the authenticator of a user, like the account page does. The clock then moves on to the next step.</summary>
    public static async Task<EnrolledUser> EnrolAsync(this TestHost host, TestClock clock, User user, Guid? keepSession = null)
    {
        using IServiceScope scope = host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<TwoFactorService>();

        (string? error, string? secret) = await service.BeginEnrolmentAsync(user.Id);
        Assert.Null(error);
        Assert.True(Base32.TryDecode(secret, out byte[] key));

        (error, IReadOnlyList<string> codes) = await service.ConfirmEnrolmentAsync(user.Id, Totp.Compute(key, clock.Step), keepSession, "127.0.0.1");
        Assert.Null(error);
        clock.NextStep();
        return new EnrolledUser(user, key, codes);
    }

    /// <summary>Creates a user of the tenant with the given roles.</summary>
    public static async Task<User> CreateUserAsync(this TestHost host, long tenantId, string login, bool isSystemAdmin = false, params long[] roleIds)
    {
        using IServiceScope scope = host.Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        (User? user, string? error) = await users.CreateAsync(
            new UserInput { LoginName = login, DisplayName = login, Password = Password, IsSystemAdmin = isSystemAdmin, RoleIds = roleIds, CreateMailbox = false },
            tenantId);
        Assert.Null(error);
        return user!;
    }

    public static async Task<Role> CreateRoleAsync(this TestHost host, long tenantId, string name, bool requiresTwoFactor = false, params string[] permissions)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var role = new Role { TenantId = tenantId, Name = name, Permissions = permissions, RequiresTwoFactor = requiresTwoFactor };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    public static async Task SetPolicyAsync(this TestHost host, long tenantId, TwoFactorMode mode)
    {
        using IServiceScope scope = host.Scope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<TwoFactorPolicy>().SetModeAsync(tenantId, mode));
    }

    public static async Task<T> ReadAsync<T>(this TestHost host, Func<MatMailDbContext, Task<T>> query)
    {
        using IServiceScope scope = host.Scope();
        return await query(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    /// <summary>Changes rows the way the application does and saves them (a scope of its own, acting as the system).</summary>
    public static async Task WriteAsync(this TestHost host, Func<MatMailDbContext, Task> change)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    public static Task<User> ReloadAsync(this TestHost host, User user)
        => host.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == user.Id));

    /// <summary>The second step of a web sign-in.</summary>
    public static async Task<SecondStepOutcome> VerifyLoginAsync(this TestHost host, User user, string? code)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<TwoFactorService>().VerifyLoginAsync(user.Id, code, "127.0.0.1");
    }

    public static async Task<SignInOutcome> SignInAsync(this TestHost host, string login, string? secret, SignInPurpose purpose, string ip = "127.0.0.1")
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>().ValidateCredentialsAsync(login, secret, ip, purpose);
    }

    /// <summary>An app password for the user, the way the account page creates it.</summary>
    public static async Task<string> CreateAppPasswordAsync(this TestHost host, User user, string name = "Thunderbird")
    {
        using IServiceScope scope = host.Scope();
        (string? error, string? password) = await scope.ServiceProvider.GetRequiredService<TwoFactorService>()
            .CreateAppPasswordAsync(user.Id, name, Password, "127.0.0.1");
        Assert.Null(error);
        return password!;
    }
}

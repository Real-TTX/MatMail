using System.Security.Claims;
using MatMail.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

public enum SignInStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
}

public sealed record SignInOutcome(SignInStatus Status, User? User)
{
    public bool Succeeded => Status == SignInStatus.Success;
}

/// <summary>
/// Local sign-in: checks passwords (also for IMAP/SMTP clients), keeps the server-side session table and issues the
/// session cookie. The cookie only carries the session token; the row in <c>UserSession</c> decides whether it is valid,
/// which is why sessions survive a container restart.
/// </summary>
public sealed class SignInService
{
    public const int MinPasswordLength = 10;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly MatMailDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly PasswordHasher<User> _hasher;
    private readonly SessionCache _cache;
    private readonly ActivityLogger _log;

    // Verified against when the login name is unknown, so "no such user" costs as much time as "wrong password".
    private static readonly User TimingUser = new() { LoginName = "timing" };
    private string? _timingHash;

    public SignInService(MatMailDbContext db, IHttpContextAccessor http, PasswordHasher<User> hasher, SessionCache cache, ActivityLogger log)
    {
        _db = db;
        _http = http;
        _hasher = hasher;
        _cache = cache;
        _log = log;
    }

    public static string NormalizeLoginName(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public Task<bool> AnyUsersExistAsync() => _db.Users.IgnoreQueryFilters().AnyAsync();

    public string HashPassword(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>Fixed text (a localization key); keep the number in sync with <see cref="MinPasswordLength"/>.</summary>
    public const string PasswordTooShortMessage = "The password must have at least 10 characters.";

    public static string? ValidatePasswordStrength(string? password)
        => string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? PasswordTooShortMessage : null;

    /// <summary>Checks a login name and password. Used by the web sign-in page and by the IMAP and SMTP servers.</summary>
    public async Task<SignInOutcome> ValidateCredentialsAsync(string? loginName, string? password, string? remoteIp = null)
    {
        string name = NormalizeLoginName(loginName);
        password ??= string.Empty;
        DateTime now = DateTime.UtcNow;

        User? user = name.Length == 0 ? null : await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.LoginName == name);
        if (user is null)
        {
            _timingHash ??= _hasher.HashPassword(TimingUser, "timing-only-password");
            _hasher.VerifyHashedPassword(TimingUser, _timingHash, password);
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in failed: unknown user '{Truncate(name)}'.", remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.InvalidCredentials, null);
        }

        if (user.LockedUntilDate is DateTime lockedUntil && lockedUntil > now)
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is locked.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.LockedOut, null);
        }

        PasswordVerificationResult verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntilDate = now + LockoutDuration;
                user.FailedLoginCount = 0;
            }

            await _db.SaveChangesAsync();
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in failed: wrong password for '{user.LoginName}'.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.InvalidCredentials, null);
        }

        bool tenantActive = await _db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive);
        if (!user.IsActive || (!tenantActive && !user.IsSystemAdmin))
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is disabled.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.Disabled, null);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        user.FailedLoginCount = 0;
        user.LockedUntilDate = null;
        user.LastLoginDate = now;
        await _db.SaveChangesAsync();
        return new SignInOutcome(SignInStatus.Success, user);
    }

    /// <summary>Creates the session row and the cookie for an already verified user.</summary>
    public async Task SignInAsync(User user, bool persistent = true)
    {
        HttpContext http = _http.HttpContext ?? throw new InvalidOperationException("No active HttpContext.");
        DateTime now = DateTime.UtcNow;

        string userAgent = http.Request.Headers.UserAgent.ToString();
        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            TenantId = user.TenantId,
            ExpiresDate = now + SessionLifetime,
            LastSeenDate = now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length == 0 ? null : userAgent[..Math.Min(userAgent.Length, 512)],
            CreateUserId = user.Id,
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();

        SessionSnapshot snapshot = (await LoadSnapshotAsync(session.Token, touch: false))!;
        _cache.Set(session.Token, snapshot);

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            BuildPrincipal(snapshot, session.Token),
            new AuthenticationProperties { IsPersistent = persistent, ExpiresUtc = session.ExpiresDate });

        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' signed in.", tenantId: user.TenantId, userId: user.Id, remoteIp: session.IpAddress);
    }

    public async Task SignOutAsync()
    {
        HttpContext http = _http.HttpContext ?? throw new InvalidOperationException("No active HttpContext.");
        if (Guid.TryParse(http.User.FindFirstValue(AppClaims.SessionToken), out Guid token))
        {
            await RevokeAsync(token);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>Deletes a session; the cookie that carries its token stops working immediately.</summary>
    public async Task RevokeAsync(Guid token)
    {
        await _db.UserSessions.Where(s => s.Token == token).ExecuteDeleteAsync();
        _cache.Invalidate(token);
    }

    /// <summary>
    /// Loads what the principal is built from. Returns null when the session is unknown, expired, or the user is disabled.
    /// With <paramref name="touch"/> the session is kept alive (sliding expiration) at most every few minutes.
    /// </summary>
    public async Task<SessionSnapshot?> LoadSnapshotAsync(Guid token, bool touch = true)
    {
        DateTime now = DateTime.UtcNow;
        UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session is null || session.ExpiresDate < now)
        {
            return null;
        }

        User? user = await _db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        // Only system administrators may work in a tenant other than their own.
        long tenantId = user.IsSystemAdmin ? session.TenantId : user.TenantId;
        Tenant? tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null || (!tenant.IsActive && !user.IsSystemAdmin))
        {
            return null;
        }

        List<string[]> rolePermissions = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role!.Permissions)
            .ToListAsync();
        string[] permissions = rolePermissions.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToArray();

        if (touch && session.LastSeenDate < now - TouchInterval)
        {
            session.LastSeenDate = now;
            session.ExpiresDate = now + SessionLifetime;
            await _db.SaveChangesAsync();
        }

        return new SessionSnapshot(
            user.Id,
            tenant.Id,
            tenant.Name,
            user.TenantId,
            user.LoginName,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.LoginName : user.DisplayName,
            user.IsSystemAdmin,
            user.MustChangePassword,
            permissions,
            user.ThemeMode,
            user.ThemeAccent,
            user.Culture,
            session.ExpiresDate);
    }

    public static ClaimsPrincipal BuildPrincipal(SessionSnapshot snapshot, Guid token)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, snapshot.UserId.ToString()),
            new(ClaimTypes.Name, snapshot.LoginName),
            new(AppClaims.DisplayName, snapshot.DisplayName),
            new(AppClaims.TenantId, snapshot.TenantId.ToString()),
            new(AppClaims.TenantName, snapshot.TenantName),
            new(AppClaims.HomeTenantId, snapshot.HomeTenantId.ToString()),
            new(AppClaims.SystemAdmin, snapshot.IsSystemAdmin ? "1" : "0"),
            new(AppClaims.MustChangePassword, snapshot.MustChangePassword ? "1" : "0"),
            new(AppClaims.SessionToken, token.ToString()),
        };

        claims.AddRange(snapshot.Permissions.Select(p => new Claim(AppClaims.Permission, p)));
        AddIfSet(claims, AppClaims.ThemeMode, snapshot.ThemeMode);
        AddIfSet(claims, AppClaims.ThemeAccent, snapshot.ThemeAccent);
        AddIfSet(claims, AppClaims.Culture, snapshot.Culture);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>System administrators switch the tenant they are working in.</summary>
    public async Task<bool> SwitchTenantAsync(Guid token, long tenantId)
    {
        UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session is null || !await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == session.UserId && u.IsSystemAdmin))
        {
            return false;
        }

        if (!await _db.Tenants.AnyAsync(t => t.Id == tenantId))
        {
            return false;
        }

        session.TenantId = tenantId;
        await _db.SaveChangesAsync();
        _cache.Invalidate(token);
        return true;
    }

    private static void AddIfSet(List<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(type, value));
        }
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80];
}

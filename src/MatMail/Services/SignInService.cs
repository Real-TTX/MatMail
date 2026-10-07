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

/// <summary>Where the credentials are used: the web sign-in page (password, then a code if two-factor authentication is on) or a mail protocol.</summary>
public enum SignInPurpose
{
    Web,

    /// <summary>IMAP and SMTP cannot ask for a second factor: with two-factor authentication on or required they take app passwords only.</summary>
    Protocol,
}

/// <summary>What was accepted as the credential.</summary>
public enum SignInMethod
{
    Password,
    AppPassword,
}

/// <param name="TwoFactor">The state of the user, when the credentials were right.</param>
/// <param name="SecondFactorPending">Web sign-in only: the password was right, the session may only be created after a code.</param>
public sealed record SignInOutcome(
    SignInStatus Status,
    User? User,
    TwoFactorStatus? TwoFactor = null,
    SignInMethod Method = SignInMethod.Password,
    bool SecondFactorPending = false)
{
    public bool Succeeded => Status == SignInStatus.Success;
}

/// <summary>
/// Local sign-in: checks passwords and app passwords (also for IMAP/SMTP clients), keeps the server-side session table and issues the
/// session cookie. The cookie only carries the session token; the row in <c>UserSession</c> decides whether it is valid,
/// which is why sessions survive a container restart.
/// </summary>
public sealed class SignInService
{
    public const int MinPasswordLength = 10;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>Fixed text (a localization key) for everything that is refused because the account is locked.</summary>
    public const string LockedMessage = "Too many failed attempts. Please try again in a few minutes.";

    public const string WrongPasswordMessage = "The password is wrong.";

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly MatMailDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly PasswordHasher<User> _hasher;
    private readonly SessionCache _cache;
    private readonly ActivityLogger _log;
    private readonly TwoFactorPolicy _twoFactor;
    private readonly AppPasswordService _appPasswords;

    // Verified against when the login name is unknown, so "no such user" costs as much time as "wrong password".
    private static readonly User TimingUser = new() { LoginName = "timing" };
    private string? _timingHash;

    public SignInService(
        MatMailDbContext db,
        IHttpContextAccessor http,
        PasswordHasher<User> hasher,
        SessionCache cache,
        ActivityLogger log,
        TwoFactorPolicy twoFactor,
        AppPasswordService appPasswords)
    {
        _db = db;
        _http = http;
        _hasher = hasher;
        _cache = cache;
        _log = log;
        _twoFactor = twoFactor;
        _appPasswords = appPasswords;
    }

    public static string NormalizeLoginName(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public Task<bool> AnyUsersExistAsync() => _db.Users.IgnoreQueryFilters().AnyAsync();

    public string HashPassword(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>Fixed text (a localization key); keep the number in sync with <see cref="MinPasswordLength"/>.</summary>
    public const string PasswordTooShortMessage = "The password must have at least 10 characters.";

    public static string? ValidatePasswordStrength(string? password)
        => string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? PasswordTooShortMessage : null;

    /// <summary>
    /// The user for a login name. Mail programs offer the e-mail address as the user name, so the address of a personal mailbox
    /// signs in its owner as well (addresses are unique across all tenants).
    /// </summary>
    private async Task<User?> FindUserAsync(string name)
    {
        User? user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.LoginName == name);
        if (user is not null || !name.Contains('@'))
        {
            return user;
        }

        return await _db.MailboxAliases.IgnoreQueryFilters()
            .Where(a => a.Address == name && a.Mailbox!.Type == MailboxType.Personal && a.Mailbox.OwnerUserId != null)
            .Select(a => a.Mailbox!.OwnerUser)
            .FirstOrDefaultAsync();
    }

    /// <summary>The account is locked after too many failures (see <see cref="RegisterFailureAsync"/>).</summary>
    public static bool IsLocked(User user, DateTime now) => user.LockedUntilDate is DateTime lockedUntil && lockedUntil > now;

    /// <summary>
    /// Checks a login name and a password or app password. Used by the web sign-in page and the account pages (<see cref="SignInPurpose.Web"/>:
    /// the password only; with two-factor authentication on, the code is a second step and the failures stay counted until it is done)
    /// and by the IMAP and SMTP servers (<see cref="SignInPurpose.Protocol"/>: an app password, or the password while two-factor
    /// authentication is neither on nor required). App passwords are never accepted for the web.
    /// </summary>
    public async Task<SignInOutcome> ValidateCredentialsAsync(
        string? loginName, string? password, string? remoteIp = null, SignInPurpose purpose = SignInPurpose.Web)
    {
        string name = NormalizeLoginName(loginName);
        password ??= string.Empty;
        DateTime now = DateTime.UtcNow;

        User? user = name.Length == 0 ? null : await FindUserAsync(name);
        if (user is null)
        {
            _timingHash ??= _hasher.HashPassword(TimingUser, "timing-only-password");
            _hasher.VerifyHashedPassword(TimingUser, _timingHash, password);
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in failed: unknown user '{Truncate(name)}'.", remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.InvalidCredentials, null);
        }

        if (IsLocked(user, now))
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is locked.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.LockedOut, null);
        }

        TwoFactorStatus twoFactor = await _twoFactor.GetStatusAsync(user);
        CredentialCheck check = purpose == SignInPurpose.Protocol
            ? await CheckProtocolCredentialsAsync(user, password, twoFactor)
            : new CredentialCheck(_hasher.VerifyHashedPassword(user, user.PasswordHash, password));
        if (!check.Accepted)
        {
            // A right password that the protocol does not take any more (two-factor authentication came) is no guess: counting it would
            // lock people out of the web, where they have to go to create their app password, every time an old mail program retries.
            if (!check.PasswordRefused)
            {
                await RegisterFailureAsync(user.Id, now);
            }

            await _log.WarnAsync(ActivityCategory.Auth, check.FailureMessage(user), tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.InvalidCredentials, null);
        }

        if (!await CanSignInAsync(user))
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is disabled.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SignInOutcome(SignInStatus.Disabled, null);
        }

        if (check.Verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        // With a second step to come the sign-in is not complete: the failures stay counted, so codes cannot be guessed by passing
        // the password step again and again. The second step forgets them (CompleteSignInAsync).
        bool secondFactorPending = purpose == SignInPurpose.Web && twoFactor.Enabled;
        if (!secondFactorPending)
        {
            user.FailedLoginCount = 0;
            user.LockedUntilDate = null;
            user.LastLoginDate = now;
        }

        await _db.SaveChangesAsync();
        if (check.AppPassword is not null)
        {
            await _appPasswords.RecordUseAsync(check.AppPassword, remoteIp, now);
        }

        return new SignInOutcome(SignInStatus.Success, user, twoFactor, check.Method, secondFactorPending);
    }

    /// <summary>
    /// IMAP and SMTP: an app password is always good. The account password only counts while two-factor authentication is neither on nor
    /// required, because these protocols cannot ask for a code. The same work is done whatever the user has, so the time taken says
    /// nothing about it (and the answer to the client is the same too).
    /// </summary>
    private async Task<CredentialCheck> CheckProtocolCredentialsAsync(User user, string password, TwoFactorStatus twoFactor)
    {
        AppPassword? appPassword = await _appPasswords.FindMatchAsync(user, password);
        if (appPassword is not null)
        {
            return new CredentialCheck(PasswordVerificationResult.Success, SignInMethod.AppPassword, appPassword);
        }

        PasswordVerificationResult verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return verification != PasswordVerificationResult.Failed && twoFactor.ProtocolsNeedAppPassword
            ? new CredentialCheck(PasswordVerificationResult.Failed, PasswordRefused: true)
            : new CredentialCheck(verification);
    }

    /// <summary>What a credential check found. A refused password (right, but not good enough here) is told apart for the log only.</summary>
    private sealed record CredentialCheck(
        PasswordVerificationResult Verification,
        SignInMethod Method = SignInMethod.Password,
        AppPassword? AppPassword = null,
        bool PasswordRefused = false)
    {
        public bool Accepted => Verification != PasswordVerificationResult.Failed;

        public string FailureMessage(User user)
            => PasswordRefused
                ? $"Sign-in refused: '{user.LoginName}' has two-factor authentication; IMAP and SMTP accept an app password only."
                : $"Sign-in failed: wrong password for '{user.LoginName}'.";
    }

    /// <summary>May this user sign in at all: active, and in a tenant that is switched on (system administrators sign in anyway)?</summary>
    public async Task<bool> CanSignInAsync(User user)
    {
        bool tenantActive = await _db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive);
        return user.IsActive && (tenantActive || user.IsSystemAdmin);
    }

    /// <summary>
    /// Counts a failed attempt (wrong password, wrong code, wrong app password): after <see cref="MaxFailedAttempts"/> the account is
    /// locked for <see cref="LockoutDuration"/>. One statement, so guesses that run in parallel cannot overwrite each other's count.
    /// </summary>
    public async Task RegisterFailureAsync(long userId, DateTime now)
    {
        DateTime lockUntil = now + LockoutDuration;
        int limit = MaxFailedAttempts;
        await _db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= limit ? 0 : u.FailedLoginCount + 1)
            .SetProperty(u => u.LockedUntilDate, u => u.FailedLoginCount + 1 >= limit ? (DateTime?)lockUntil : u.LockedUntilDate)
            .SetProperty(u => u.UpdateDate, now));
    }

    /// <summary>The second step of a web sign-in was passed: the failures of this attempt are forgotten and the time is noted.</summary>
    public async Task CompleteSignInAsync(long userId, DateTime now)
        => await _db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.LockedUntilDate, (DateTime?)null)
            .SetProperty(u => u.LastLoginDate, now)
            .SetProperty(u => u.UpdateDate, now));

    /// <summary>
    /// Asks a signed-in user for the password again before something sensitive (two-factor settings, app passwords). A wrong password
    /// counts like one at sign-in. Returns an English error text, or null when the password is right.
    /// </summary>
    public async Task<string?> ConfirmPasswordAsync(User user, string? password, string? remoteIp)
    {
        DateTime now = DateTime.UtcNow;
        if (IsLocked(user, now))
        {
            return LockedMessage;
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) != PasswordVerificationResult.Failed)
        {
            return null;
        }

        await RegisterFailureAsync(user.Id, now);
        await _log.WarnAsync(ActivityCategory.Auth, $"Password check failed: wrong password for '{user.LoginName}'.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
        return WrongPasswordMessage;
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
            IpAddress = http.ClientAddress(),
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

        var roles = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => new { ur.Role!.Permissions, ur.Role.RequiresTwoFactor })
            .ToListAsync();
        string[] permissions = roles.SelectMany(r => r.Permissions).Distinct(StringComparer.Ordinal).ToArray();

        // The policy of the tenant the user belongs to, not the one a system administrator is working in right now.
        TwoFactorMode twoFactorMode = tenant.Id == user.TenantId ? tenant.TwoFactorMode : await _twoFactor.GetModeAsync(user.TenantId);
        bool twoFactorEnabled = await _db.UserTotps.AnyAsync(t => t.UserId == user.Id && t.ConfirmedDate != null);
        bool twoFactorRequired = TwoFactorPolicy.IsRequired(twoFactorMode, user.IsSystemAdmin, permissions, roles.Any(r => r.RequiresTwoFactor));

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
            session.ExpiresDate,
            twoFactorEnabled,
            twoFactorRequired);
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
            new(AppClaims.TwoFactor, snapshot.TwoFactorEnabled ? "1" : "0"),
            new(AppClaims.TwoFactorRequired, snapshot.TwoFactorRequired ? "1" : "0"),
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

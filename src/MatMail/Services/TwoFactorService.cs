using System.Security.Cryptography;
using System.Text;
using MatMail.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

public enum SecondFactorStatus
{
    Accepted,
    Wrong,
    LockedOut,
}

/// <summary>How a code was checked: wrong, accepted, or not checked because the account is locked.</summary>
public sealed record SecondFactorResult(SecondFactorStatus Status, bool UsedRecoveryCode = false, int RecoveryCodesLeft = 0)
{
    public bool Accepted => Status == SecondFactorStatus.Accepted;
}

/// <summary>The second step of a web sign-in: the verdict, and the user when the code was right and they may sign in.</summary>
public sealed record SecondStepOutcome(SecondFactorResult Result, User? User);

/// <summary>What the security page shows about the signed-in user.</summary>
public sealed record TwoFactorOverview(TwoFactorStatus Status, DateTime? EnabledSince, int RecoveryCodesLeft, string? PendingSecret);

/// <summary>
/// Two-factor authentication of a user: enrolling an authenticator app (TOTP) and confirming it, the second step of the web sign-in,
/// recovery codes, turning it off, the reset by an administrator, and app passwords (which need the password again). Wrong codes
/// count against the same lockout as wrong passwords. Errors come back as English source strings.
/// </summary>
public sealed class TwoFactorService
{
    public const int RecoveryCodeCount = 10;

    public const string WrongCodeMessage = "The code is wrong or was already used.";

    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private const int RecoveryCodeLength = 10;

    private readonly MatMailDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly PasswordHasher<User> _hasher;
    private readonly SignInService _signIn;
    private readonly TwoFactorPolicy _policy;
    private readonly AppPasswordService _appPasswords;
    private readonly SessionCache _cache;
    private readonly ActivityLogger _log;
    private readonly CurrentUser _current;
    private readonly TimeProvider _time;

    public TwoFactorService(
        MatMailDbContext db,
        SecretProtector secrets,
        PasswordHasher<User> hasher,
        SignInService signIn,
        TwoFactorPolicy policy,
        AppPasswordService appPasswords,
        SessionCache cache,
        ActivityLogger log,
        CurrentUser current,
        TimeProvider time)
    {
        _db = db;
        _secrets = secrets;
        _hasher = hasher;
        _signIn = signIn;
        _policy = policy;
        _appPasswords = appPasswords;
        _cache = cache;
        _log = log;
        _current = current;
        _time = time;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // -------------------------------------------------------------------------------------------------------------------
    // Recovery code text
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>A new recovery code in the form people see: "abcde-fghij".</summary>
    public static string GenerateRecoveryCode()
    {
        var characters = new char[RecoveryCodeLength];
        for (int i = 0; i < characters.Length; i++)
        {
            characters[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        }

        return new string(characters, 0, 5) + "-" + new string(characters, 5, 5);
    }

    /// <summary>The ten characters behind what was typed (hyphens and spaces ignored, capitals fine), or null when it is no recovery code.</summary>
    public static string? NormalizeRecoveryCode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 40)
        {
            return null;
        }

        var normalized = new StringBuilder(RecoveryCodeLength);
        foreach (char c in text)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (RecoveryAlphabet.IndexOf(lower) < 0 || normalized.Length >= RecoveryCodeLength)
            {
                return null;
            }

            normalized.Append(lower);
        }

        return normalized.Length == RecoveryCodeLength ? normalized.ToString() : null;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------------------------------------------------

    public async Task<TwoFactorStatus> GetStatusAsync(long userId, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        return user is null ? TwoFactorStatus.Off : await _policy.GetStatusAsync(user, cancel);
    }

    public async Task<TwoFactorOverview> GetOverviewAsync(long userId, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        if (user is null)
        {
            return new TwoFactorOverview(TwoFactorStatus.Off, null, 0, null);
        }

        TwoFactorStatus status = await _policy.GetStatusAsync(user, cancel);
        UserTotp? row = await _db.UserTotps.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, cancel);
        int codesLeft = await _db.UserRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedDate == null, cancel);
        string? pending = row is { ConfirmedDate: null } ? _secrets.Unprotect(row.Secret) : null;
        return new TwoFactorOverview(status, row?.ConfirmedDate, codesLeft, pending);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Enrolment
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the set-up with a fresh secret (a set-up that was started before is replaced). The authenticator only counts once a
    /// code from it was confirmed. Returns the secret as Base32 text for the QR code and the manual entry, or an error.
    /// </summary>
    public async Task<(string? Error, string? Secret)> BeginEnrolmentAsync(long userId, CancellationToken cancel = default)
    {
        UserTotp? row = await _db.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId, cancel);
        if (row?.ConfirmedDate is not null)
        {
            return ("Two-factor authentication is already on.", null);
        }

        string secret = Base32.Encode(Totp.NewSecret());
        if (row is null)
        {
            row = new UserTotp { UserId = userId };
            _db.UserTotps.Add(row);
        }

        row.Secret = _secrets.Protect(secret);
        row.LastUsedStep = 0;
        await _db.SaveChangesAsync(cancel);
        return (null, secret);
    }

    /// <summary>
    /// Starts the set-up for a person at the web page: asks for the password first, because a stolen session must not be able to switch
    /// on a second factor of its own (which would lock the owner out).
    /// </summary>
    public async Task<(string? Error, string? Secret)> StartEnrolmentAsync(long userId, string? password, string? remoteIp, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        if (user is null)
        {
            return ("The user does not exist.", null);
        }

        string? error = await _signIn.ConfirmPasswordAsync(user, password, remoteIp);
        return error is not null ? (error, null) : await BeginEnrolmentAsync(userId, cancel);
    }

    /// <summary>Drops a set-up that was started but not confirmed.</summary>
    public Task CancelEnrolmentAsync(long userId, CancellationToken cancel = default)
        => _db.UserTotps.Where(t => t.UserId == userId && t.ConfirmedDate == null).ExecuteDeleteAsync(cancel);

    /// <summary>
    /// Confirms the set-up with a code from the app: two-factor authentication is on from now on. Creates the recovery codes (returned
    /// in the clear, this once) and ends the user's other sessions, which were opened without a second factor.
    /// </summary>
    public async Task<(string? Error, IReadOnlyList<string> RecoveryCodes)> ConfirmEnrolmentAsync(long userId, string? code, Guid? keepSession, string? remoteIp, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        UserTotp? row = await _db.UserTotps.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId && t.ConfirmedDate == null, cancel);
        if (user is null || row is null)
        {
            return ("Start the set-up first.", Array.Empty<string>());
        }

        byte[]? secret = DecodeSecret(row);
        if (secret is null)
        {
            return ("The set-up has to be started again.", Array.Empty<string>());
        }

        if (!Totp.TryMatch(secret, code, Totp.StepOf(Now), lastUsedStep: 0, out long step))
        {
            return ("The code is wrong. Check that the time on your phone is right, then try again.", Array.Empty<string>());
        }

        DateTime now = Now.UtcDateTime;
        int switched = await _db.UserTotps.Where(t => t.Id == row.Id && t.ConfirmedDate == null).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.ConfirmedDate, now)
            .SetProperty(t => t.LastUsedStep, step)
            .SetProperty(t => t.UpdateDate, now), cancel);
        if (switched != 1)
        {
            return ("Two-factor authentication is already on.", Array.Empty<string>());
        }

        IReadOnlyList<string> recoveryCodes = await ReplaceRecoveryCodesAsync(user, cancel);
        await _db.UserSessions.Where(s => s.UserId == userId && (keepSession == null || s.Token != keepSession)).ExecuteDeleteAsync(cancel);
        _cache.InvalidateUser(userId);

        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' turned on two-factor authentication.", tenantId: user.TenantId, userId: userId, remoteIp: remoteIp);
        return (null, recoveryCodes);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Checking a code
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The second step of the web sign-in. When the code (or a recovery code) is right and the user may sign in, the failures of this
    /// attempt are forgotten and the user is returned for the session to be created. Wrong codes count against the lockout.
    /// </summary>
    public async Task<SecondStepOutcome> VerifyLoginAsync(long userId, string? code, string? remoteIp, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        if (user is null)
        {
            return new SecondStepOutcome(new SecondFactorResult(SecondFactorStatus.Wrong), null);
        }

        SecondFactorResult result = await VerifyAsync(user, code, remoteIp, cancel);
        if (!result.Accepted)
        {
            return new SecondStepOutcome(result, null);
        }

        if (!await _signIn.CanSignInAsync(user))
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is disabled.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SecondStepOutcome(new SecondFactorResult(SecondFactorStatus.Wrong), null);
        }

        await _signIn.CompleteSignInAsync(user.Id, Now.UtcDateTime);
        return new SecondStepOutcome(result, user);
    }

    /// <summary>
    /// Checks a six-digit code or a recovery code of the user. A code works once: a six-digit code only for a 30 second step after the
    /// last one accepted, a recovery code until it is used. A wrong code counts against the lockout shared with wrong passwords.
    /// </summary>
    public async Task<SecondFactorResult> VerifyAsync(User user, string? input, string? remoteIp, CancellationToken cancel = default)
    {
        DateTimeOffset now = Now;
        if (SignInService.IsLocked(user, now.UtcDateTime))
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: '{user.LoginName}' is locked.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SecondFactorResult(SecondFactorStatus.LockedOut);
        }

        bool accepted = false;
        bool recovery = false;
        int left = 0;
        if (Totp.NormalizeCode(input) is not null)
        {
            accepted = await TryTotpAsync(user, input, now, cancel);
        }
        else if (NormalizeRecoveryCode(input) is string recoveryCode)
        {
            (accepted, left) = await TryRecoveryCodeAsync(user, recoveryCode, now.UtcDateTime, cancel);
            recovery = accepted;
        }

        if (!accepted)
        {
            await _signIn.RegisterFailureAsync(user.Id, now.UtcDateTime);
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in failed: wrong second factor for '{user.LoginName}'.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new SecondFactorResult(SecondFactorStatus.Wrong);
        }

        if (recovery)
        {
            await _log.WarnAsync(
                ActivityCategory.Auth, $"'{user.LoginName}' used a recovery code ({left} left).", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
        }

        return new SecondFactorResult(SecondFactorStatus.Accepted, recovery, left);
    }

    private async Task<bool> TryTotpAsync(User user, string? input, DateTimeOffset now, CancellationToken cancel)
    {
        UserTotp? row = await _db.UserTotps.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user.Id && t.ConfirmedDate != null, cancel);
        byte[]? secret = row is null ? null : DecodeSecret(row);
        if (row is null || secret is null || !Totp.TryMatch(secret, input, Totp.StepOf(now), row.LastUsedStep, out long step))
        {
            return false;
        }

        // The step is spent in one statement: of two requests that carry the same code only one gets the row.
        int spent = await _db.UserTotps.Where(t => t.Id == row.Id && t.LastUsedStep < step).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.LastUsedStep, step)
            .SetProperty(t => t.UpdateDate, now.UtcDateTime), cancel);
        return spent == 1;
    }

    private async Task<(bool Accepted, int Left)> TryRecoveryCodeAsync(User user, string normalized, DateTime now, CancellationToken cancel)
    {
        List<UserRecoveryCode> open = await _db.UserRecoveryCodes.AsNoTracking().Where(c => c.UserId == user.Id && c.UsedDate == null).ToListAsync(cancel);
        UserRecoveryCode? match = null;
        foreach (UserRecoveryCode code in open)
        {
            if (match is null && _hasher.VerifyHashedPassword(user, code.CodeHash, normalized) != PasswordVerificationResult.Failed)
            {
                match = code;
            }
        }

        if (match is null)
        {
            return (false, open.Count);
        }

        // Used in one statement: of two requests that carry the same code only one gets the row.
        int used = await _db.UserRecoveryCodes.Where(c => c.Id == match.Id && c.UsedDate == null).ExecuteUpdateAsync(set => set
            .SetProperty(c => c.UsedDate, now)
            .SetProperty(c => c.UpdateDate, now), cancel);
        return used == 1 ? (true, open.Count - 1) : (false, open.Count);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Recovery codes, turning off, reset
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>New recovery codes instead of the old ones. Needs the password and a current code (or a recovery code).</summary>
    public async Task<(string? Error, IReadOnlyList<string> RecoveryCodes)> RegenerateRecoveryCodesAsync(long userId, string? password, string? code, string? remoteIp, CancellationToken cancel = default)
    {
        (User? user, string? error) = await ReauthenticateAsync(userId, password, code, remoteIp, cancel);
        if (user is null)
        {
            return (error, Array.Empty<string>());
        }

        IReadOnlyList<string> codes = await ReplaceRecoveryCodesAsync(user, cancel);
        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' created new recovery codes.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
        return (null, codes);
    }

    /// <summary>Turns two-factor authentication off. Needs the password and a current code (or a recovery code); the recovery codes are deleted.</summary>
    public async Task<string?> DisableAsync(long userId, string? password, string? code, string? remoteIp, CancellationToken cancel = default)
    {
        (User? user, string? error) = await ReauthenticateAsync(userId, password, code, remoteIp, cancel);
        if (user is null)
        {
            return error;
        }

        await RemoveAsync(userId, cancel);
        _cache.InvalidateUser(userId);
        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' turned off two-factor authentication.", tenantId: user.TenantId, userId: userId, remoteIp: remoteIp);
        return null;
    }

    /// <summary>
    /// An administrator takes two-factor authentication away from a user (a lost phone): the authenticator and the recovery codes are
    /// deleted and the sessions end, so the user signs in with the password again (and sets it up again if it is required). App
    /// passwords stay. Needs "manage users"; only a system administrator may do this for a system administrator, and nobody for themselves.
    /// </summary>
    public async Task<string?> ResetAsync(long userId, CancellationToken cancel = default)
    {
        if (!_current.Can(Permissions.UsersManage))
        {
            return "You are not allowed to do this.";
        }

        // The tenant filter applies: administrators reach the users of the tenant they work in.
        User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancel);
        if (user is null)
        {
            return "The user does not exist.";
        }

        if (user.IsSystemAdmin && !_current.IsSystemAdmin)
        {
            return "Only system administrators can change a system administrator.";
        }

        if (_current.UserId == userId)
        {
            return "You cannot reset your own two-factor authentication here. Turn it off under Security in your account.";
        }

        if (!await _db.UserTotps.AnyAsync(t => t.UserId == userId, cancel))
        {
            return "Two-factor authentication is not on for this user.";
        }

        await RemoveAsync(userId, cancel);
        await _db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancel);
        _cache.InvalidateUser(userId);

        await _log.InfoAsync(
            ActivityCategory.Admin,
            $"Two-factor authentication of '{user.LoginName}' was reset by '{_current.DisplayName ?? _current.Username ?? "the system"}'.",
            tenantId: user.TenantId,
            userId: _current.UserId);
        return null;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // App passwords
    // -------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AppPassword>> ListAppPasswordsAsync(long userId, CancellationToken cancel = default)
        => await _appPasswords.ListAsync(userId, cancel);

    /// <summary>
    /// A new app password for a mail program. Asks for the password again: an app password signs in to IMAP and SMTP without any
    /// second factor and outlives the web session, so a stolen session must not be able to mint one. Not before the second factor
    /// is set up when the user is bound to it.
    /// </summary>
    public async Task<(string? Error, string? Password)> CreateAppPasswordAsync(long userId, string? name, string? password, string? remoteIp, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        if (user is null)
        {
            return ("The user does not exist.", null);
        }

        if ((await _policy.GetStatusAsync(user, cancel)).SetupRequired)
        {
            return ("Set up two-factor authentication first.", null);
        }

        string? error = await _signIn.ConfirmPasswordAsync(user, password, remoteIp);
        if (error is not null)
        {
            return (error, null);
        }

        return await _appPasswords.CreateAsync(user, name, cancel);
    }

    public async Task<string?> RevokeAppPasswordAsync(long userId, Guid token, CancellationToken cancel = default)
    {
        User? user = await FindUserAsync(userId, cancel);
        return user is null ? "The user does not exist." : await _appPasswords.RevokeAsync(user, token, cancel);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>Password and a current code: what sensitive changes to the second factor ask for.</summary>
    private async Task<(User? User, string? Error)> ReauthenticateAsync(long userId, string? password, string? code, string? remoteIp, CancellationToken cancel)
    {
        User? user = await FindUserAsync(userId, cancel);
        if (user is null)
        {
            return (null, "The user does not exist.");
        }

        if (!await _db.UserTotps.AnyAsync(t => t.UserId == userId && t.ConfirmedDate != null, cancel))
        {
            return (null, "Two-factor authentication is not on.");
        }

        string? passwordError = await _signIn.ConfirmPasswordAsync(user, password, remoteIp);
        if (passwordError is not null)
        {
            return (null, passwordError);
        }

        SecondFactorResult result = await VerifyAsync(user, code, remoteIp, cancel);
        return result.Status switch
        {
            SecondFactorStatus.Accepted => (user, null),
            SecondFactorStatus.LockedOut => (null, SignInService.LockedMessage),
            _ => (null, WrongCodeMessage),
        };
    }

    /// <summary>Deletes the authenticator and the recovery codes of a user.</summary>
    private async Task RemoveAsync(long userId, CancellationToken cancel)
    {
        await _db.UserRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancel);
        await _db.UserTotps.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancel);
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(User user, CancellationToken cancel)
    {
        var codes = new List<string>(RecoveryCodeCount);
        while (codes.Count < RecoveryCodeCount)
        {
            string code = GenerateRecoveryCode();
            if (!codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        // The old codes go and the new ones arrive together: a failure in between must not leave the user without any.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancel);
        await _db.UserRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancel);
        foreach (string code in codes)
        {
            _db.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = user.Id, CodeHash = _hasher.HashPassword(user, NormalizeRecoveryCode(code)!) });
        }

        await _db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
        return codes;
    }

    private byte[]? DecodeSecret(UserTotp row)
    {
        string? text = _secrets.Unprotect(row.Secret);
        return text is not null && Base32.TryDecode(text, out byte[] secret) ? secret : null;
    }

    /// <summary>Read fresh from the database every time (the failure counter is changed by single statements, a tracked copy would be stale).</summary>
    private Task<User?> FindUserAsync(long userId, CancellationToken cancel)
        => _db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancel);
}

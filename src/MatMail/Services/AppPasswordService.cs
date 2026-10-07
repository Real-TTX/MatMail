using System.Security.Cryptography;
using System.Text;
using MatMail.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// App passwords: what a mail program (IMAP, SMTP) uses instead of the account password once two-factor authentication is on,
/// because those protocols cannot ask for a code. 24 random characters of an alphabet without look-alikes (about 119 bits), shown
/// once as "abcd-efgh-jkmn-pqrs-tuvw-xyz2", stored as a PBKDF2 hash. The first four characters are kept in the clear: they find the
/// right row at sign-in, so one hash is verified instead of all of them. An app password is never accepted on the web sign-in page.
/// </summary>
public sealed class AppPasswordService
{
    public const int MaxPerUser = 20;
    public const int MaxNameLength = 100;

    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private const int Length = 24;
    private const int GroupSize = 4;
    private const int PrefixLength = 4;
    private static readonly TimeSpan UsedWriteInterval = TimeSpan.FromMinutes(1);

    // Verified against when no app password matches, so a guess that hits a stored prefix costs what any other guess costs.
    private static readonly User TimingUser = new() { LoginName = "timing" };
    private static readonly Lazy<string> TimingHash = new(() => new PasswordHasher<User>().HashPassword(TimingUser, "timing-only-app-password"));

    private readonly MatMailDbContext _db;
    private readonly PasswordHasher<User> _hasher;
    private readonly ActivityLogger _log;

    public AppPasswordService(MatMailDbContext db, PasswordHasher<User> hasher, ActivityLogger log)
    {
        _db = db;
        _hasher = hasher;
        _log = log;
    }

    /// <summary>A new random app password, 24 characters without separators.</summary>
    public static string Generate()
    {
        var characters = new char[Length];
        for (int i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(characters);
    }

    /// <summary>The form people see and copy: groups of four, separated by hyphens.</summary>
    public static string Format(string password)
        => string.Join('-', Enumerable.Range(0, password.Length / GroupSize).Select(i => password.Substring(i * GroupSize, GroupSize)));

    /// <summary>
    /// The 24 characters behind what was typed (hyphens and spaces are ignored, capital letters are fine), or null when it cannot be an
    /// app password. Anything else - an ordinary password - is not looked at as an app password at all.
    /// </summary>
    public static string? Normalize(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied) || supplied.Length > 64)
        {
            return null;
        }

        var normalized = new StringBuilder(Length);
        foreach (char c in supplied)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (Alphabet.IndexOf(lower) < 0 || normalized.Length >= Length)
            {
                return null;
            }

            normalized.Append(lower);
        }

        return normalized.Length == Length ? normalized.ToString() : null;
    }

    public async Task<IReadOnlyList<AppPassword>> ListAsync(long userId, CancellationToken cancel = default)
        => await _db.AppPasswords.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.Name).ThenBy(a => a.CreateDate).ToListAsync(cancel);

    /// <summary>
    /// Creates an app password for the user. Returns the password in the form to show (this is the only time it exists in the clear)
    /// or an English error text. The caller checks that the person asked for it with their password.
    /// </summary>
    public async Task<(string? Error, string? Password)> CreateAsync(User user, string? name, CancellationToken cancel = default)
    {
        // Line breaks and the like have no place in a name (it goes into lists and into the log).
        string label = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (label.Length == 0)
        {
            return ("Enter a name for the app password.", null);
        }

        if (label.Length > MaxNameLength)
        {
            return ("The name must not be longer than 100 characters.", null);
        }

        if (await _db.AppPasswords.CountAsync(a => a.UserId == user.Id, cancel) >= MaxPerUser)
        {
            return ("You can have at most 20 app passwords. Revoke one you no longer need.", null);
        }

        string password = Generate();
        for (int attempt = 0; attempt < 5 && await PrefixInUseAsync(user.Id, password[..PrefixLength], cancel); attempt++)
        {
            password = Generate();
        }

        _db.AppPasswords.Add(new AppPassword
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            Name = label,
            Prefix = password[..PrefixLength],
            SecretHash = _hasher.HashPassword(user, password),
        });
        await _db.SaveChangesAsync(cancel);

        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' created the app password '{Shorten(label)}'.", tenantId: user.TenantId, userId: user.Id);
        return (null, Format(password));
    }

    /// <summary>Deletes an app password of the user; mail programs that use it are refused from the next sign-in on.</summary>
    public async Task<string?> RevokeAsync(User user, Guid token, CancellationToken cancel = default)
    {
        AppPassword? row = await _db.AppPasswords.FirstOrDefaultAsync(a => a.UserId == user.Id && a.Token == token, cancel);
        if (row is null)
        {
            return "The app password does not exist.";
        }

        _db.AppPasswords.Remove(row);
        await _db.SaveChangesAsync(cancel);
        await _log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' revoked the app password '{Shorten(row.Name)}'.", tenantId: user.TenantId, userId: user.Id);
        return null;
    }

    /// <summary>
    /// The app password of the user that <paramref name="supplied"/> is, or null. Text that cannot be an app password costs nothing;
    /// anything shaped like one costs exactly one hash, whether a stored password has that prefix or not.
    /// </summary>
    public async Task<AppPassword?> FindMatchAsync(User user, string? supplied, CancellationToken cancel = default)
    {
        string? password = Normalize(supplied);
        if (password is null)
        {
            return null;
        }

        string prefix = password[..PrefixLength];
        List<AppPassword> candidates = await _db.AppPasswords.AsNoTracking().Where(a => a.UserId == user.Id && a.Prefix == prefix).ToListAsync(cancel);
        if (candidates.Count == 0)
        {
            _hasher.VerifyHashedPassword(TimingUser, TimingHash.Value, password);
            return null;
        }

        AppPassword? match = null;
        foreach (AppPassword candidate in candidates)
        {
            if (_hasher.VerifyHashedPassword(user, candidate.SecretHash, password) != PasswordVerificationResult.Failed)
            {
                match ??= candidate;
            }
        }

        return match;
    }

    /// <summary>Notes when and from where an app password was used last. Mail programs sign in every few minutes, so unchanged news is not written again.</summary>
    public async Task RecordUseAsync(AppPassword password, string? remoteIp, DateTime now, CancellationToken cancel = default)
    {
        string? address = remoteIp is { Length: > 64 } ? remoteIp[..64] : remoteIp;
        if (password.LastUsedDate is DateTime last && now - last < UsedWriteInterval && password.LastUsedIp == address)
        {
            return;
        }

        await _db.AppPasswords.Where(a => a.Id == password.Id).ExecuteUpdateAsync(set => set
            .SetProperty(a => a.LastUsedDate, now)
            .SetProperty(a => a.LastUsedIp, address)
            .SetProperty(a => a.UpdateDate, now), cancel);
    }

    private Task<bool> PrefixInUseAsync(long userId, string prefix, CancellationToken cancel)
        => _db.AppPasswords.AnyAsync(a => a.UserId == userId && a.Prefix == prefix, cancel);

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60];
}

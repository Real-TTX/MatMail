using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Directories;

/// <summary>
/// A password that the directory accepted a moment ago. IMAP and SMTP clients sign in again and again (every folder, every send), and each
/// sign-in would be a round trip to the directory: this remembers a success for two minutes, per user and password (only a keyed hash is kept).
/// A password that the directory has changed since works for that long at most; nothing else is remembered, and the web never uses it.
/// </summary>
public sealed class DirectoryPasswordCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<long, (byte[] Hash, DateTime Until)> _entries = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private byte[] Hash(string password) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(password));

    public bool Knows(long userId, string password)
        => _entries.TryGetValue(userId, out var entry) && entry.Until > DateTime.UtcNow && CryptographicOperations.FixedTimeEquals(entry.Hash, Hash(password));

    public void Remember(long userId, string password) => _entries[userId] = (Hash(password), DateTime.UtcNow + Lifetime);

    public void Forget(long userId) => _entries.TryRemove(userId, out _);
}

/// <summary>
/// The users of a directory: the ones MatMail has made from its entries, how they sign in (the password is the directory's, never stored here),
/// how they are kept in step with it. Everything that creates or changes such a user is here.
/// </summary>
public sealed class DirectoryProvisioner
{
    private readonly MatMailDbContext _db;
    private readonly DirectoryService _directories;
    private readonly MailboxService _mailboxes;
    private readonly PasswordHasher<User> _hasher;
    private readonly ActivityLogger _log;
    private readonly DirectoryPasswordCache _cache;
    private readonly SessionCache _sessions;
    private readonly DirectoryAttemptLimiter _limiter;

    public DirectoryProvisioner(
        MatMailDbContext db, DirectoryService directories, MailboxService mailboxes, PasswordHasher<User> hasher, ActivityLogger log, DirectoryPasswordCache cache, SessionCache sessions,
        DirectoryAttemptLimiter limiter)
    {
        _db = db;
        _directories = directories;
        _mailboxes = mailboxes;
        _hasher = hasher;
        _log = log;
        _cache = cache;
        _sessions = sessions;
        _limiter = limiter;
    }

    /// <summary>Whether any directory could let somebody in who has no user yet (the sign-in asks only then).</summary>
    public Task<bool> AnyOpenDirectoryAsync(CancellationToken cancel = default)
        => _db.DirectoryConnections.IgnoreQueryFilters().AnyAsync(d => d.IsActive && d.CreateUsersOnSignIn, cancel);

    private Task<DirectoryConnection?> ConnectionOfAsync(User user, CancellationToken cancel)
        => user.DirectoryId is long id ? _db.DirectoryConnections.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id, cancel) : Task.FromResult<DirectoryConnection?>(null);

    // ---------------------------------------------------------------------------------------------------------------
    // The password of a user of a directory
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the directory takes this password for the user, and still lets the person in (they are there, enabled, in the group).
    /// A success refreshes what the directory says of them. <paramref name="mayRemember"/>: a success of a minute ago counts (IMAP, SMTP).
    /// </summary>
    public async Task<bool> VerifyAsync(User user, string password, bool mayRemember, CancellationToken cancel = default)
    {
        if (mayRemember && _cache.Knows(user.Id, password))
        {
            return true;
        }

        DirectoryConnection? dir = await ConnectionOfAsync(user, cancel);
        if (dir is null || !dir.IsActive)
        {
            await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: the directory of '{user.LoginName}' is not available (deleted or switched off).", tenantId: user.TenantId, userId: user.Id);
            return false;
        }

        try
        {
            DirectoryUser? entry = null;
            if (!string.IsNullOrWhiteSpace(user.DirectoryDn))
            {
                entry = await _directories.ReadUserAsync(dir, user.DirectoryDn, cancel);
            }

            if (entry is null)
            {
                // moved there since the last time, or never seen with a name: by the login
                DirectoryLookup found = await _directories.FindUserAsync(dir, user.LoginName, cancel);
                entry = found.User;
                if (entry is null)
                {
                    await _log.WarnAsync(
                        ActivityCategory.Auth,
                        found.NotAllowed ? $"Sign-in refused: '{user.LoginName}' is not in the group of the directory '{dir.Name}'." : $"Sign-in refused: '{user.LoginName}' is not in the directory '{dir.Name}' any more.",
                        tenantId: user.TenantId, userId: user.Id);
                    return false;
                }
            }

            if (entry.Disabled)
            {
                await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in refused: the account of '{user.LoginName}' is disabled in the directory '{dir.Name}'.", tenantId: user.TenantId, userId: user.Id);
                return false;
            }

            if (!await _directories.VerifyPasswordAsync(dir, entry.Dn, password, cancel))
            {
                return false;
            }

            if (Apply(dir, entry, user) || user.DirectoryDisabledDate is not null)
            {
                user.DirectoryDisabledDate = null;
                await _db.SaveChangesAsync(cancel);
            }

            if (mayRemember)
            {
                _cache.Remember(user.Id, password);
            }

            return true;
        }
        catch (DirectoryException ex)
        {
            await _log.ErrorAsync(ActivityCategory.Auth, $"The directory '{dir.Name}' could not check the sign-in of '{user.LoginName}'.", ex.Message, user.TenantId, user.Id);
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Somebody who has no user yet
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A login without a user: when a directory knows the person, lets them in and takes their password, they get a user (made from the entry,
    /// like <see cref="CreateUserAsync"/> does) and this returns it – verified already. Null when no directory takes them.
    /// </summary>
    public async Task<User?> SignInNewAsync(string login, string password, string? remoteIp, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;   // no password is no sign-in, and nothing to ask the directory (nor to count: it cannot lock anybody there)
        }

        // Every wrong password is a failed sign-in at the directory too (see DirectoryAttemptLimiter).
        if (_limiter.IsBlocked(login, remoteIp))
        {
            await _log.WarnAsync(
                ActivityCategory.Auth, $"Sign-in refused: too many failures for '{Shorten(login)}'; the directories are not asked for a while.", remoteIp: remoteIp);
            return null;
        }

        List<DirectoryConnection> directories = await _db.DirectoryConnections.IgnoreQueryFilters()
            .Where(d => d.IsActive && d.CreateUsersOnSignIn).OrderBy(d => d.Id).ToListAsync(cancel);

        bool wrongPassword = false, unreachable = false;
        foreach (DirectoryConnection dir in directories)
        {
            try
            {
                DirectoryLookup found = await _directories.FindUserAsync(dir, login, cancel);
                if (found.User is not { } entry || entry.Disabled)
                {
                    continue;
                }

                if (!await _directories.VerifyPasswordAsync(dir, entry.Dn, password, cancel))
                {
                    wrongPassword = true;
                    continue;
                }

                // The person may be a user already, under the login they had before (it was changed in the directory): then it is that user.
                User? known = await FindLinkedAsync(dir, entry, cancel);
                if (known is not null)
                {
                    if (!await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == entry.Login && u.Id != known.Id, cancel))
                    {
                        known.LoginName = entry.Login;
                    }

                    Apply(dir, entry, known);
                    known.DirectoryDisabledDate = null;
                    await _db.SaveChangesAsync(cancel);
                    _limiter.Forget(login);
                    return known;
                }

                (User? created, string? error) = await CreateUserAsync(dir, entry, cancel: cancel);
                if (created is null)
                {
                    // two sign-ins of the same new person at once: the other one has made the user
                    if (await FindLinkedAsync(dir, entry, cancel) is { } raced)
                    {
                        _limiter.Forget(login);
                        return raced;
                    }

                    await _log.WarnAsync(ActivityCategory.Auth, $"Sign-in of '{entry.Login}' from the directory '{dir.Name}' failed: {error}", tenantId: dir.TenantId, remoteIp: remoteIp);
                    continue;
                }

                _limiter.Forget(login);
                return created;
            }
            catch (DirectoryException ex)
            {
                unreachable = true;
                await _log.ErrorAsync(ActivityCategory.Auth, $"The directory '{dir.Name}' could not be asked for a sign-in.", ex.Message, dir.TenantId, remoteIp: remoteIp);
            }
        }

        if (wrongPassword)
        {
            _limiter.RecordWrongPassword(login, remoteIp);
        }
        else if (!unreachable)
        {
            _limiter.RecordUnknown(remoteIp);   // a directory that does not answer is nobody's fault
        }

        return null;
    }

    private static string Shorten(string text) => text.Length > 100 ? text[..100] + "…" : text;

    /// <summary>The user that was made from this person of the directory: found by the stable id of the entry, else by its name. Null when there is none.</summary>
    public async Task<User?> FindLinkedAsync(DirectoryConnection dir, DirectoryUser entry, CancellationToken cancel = default)
    {
        IQueryable<User> mine = _db.Users.IgnoreQueryFilters().Where(u => u.DirectoryId == dir.Id);
        if (!string.IsNullOrEmpty(entry.Uid) && await mine.FirstOrDefaultAsync(u => u.DirectoryUid == entry.Uid, cancel) is { } byUid)
        {
            return byUid;
        }

        return await mine.FirstOrDefaultAsync(u => u.DirectoryDn == entry.Dn, cancel);
    }

    /// <summary>Makes the user of an entry: the fields the mapper gives, the roles of the connection, a mailbox when it says so. A user of the directory has no password of their own.</summary>
    /// <param name="roleIds">Instead of the roles of the connection (the import page lets the administrator choose).</param>
    /// <param name="createMailbox">Instead of what the connection says.</param>
    public async Task<(User? User, string? Error)> CreateUserAsync(
        DirectoryConnection dir, DirectoryUser entry, long[]? roleIds = null, bool? createMailbox = null, CancellationToken cancel = default)
    {
        string login = SignInService.NormalizeLoginName(entry.Login);
        if (login.Length == 0 || login.Length > 320)
        {
            return (null, "The login name of the entry is empty or too long.");
        }

        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == login, cancel))
        {
            return (null, $"The login name “{login}” is already in use.");
        }

        var user = new User
        {
            TenantId = dir.TenantId,
            LoginName = login,
            IsActive = true,
            DirectoryId = dir.Id,
        };
        Apply(dir, entry, user);
        user.PasswordHash = _hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));   // never told to anybody: the directory is asked
        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException) when (_db.Entry(user).State == EntityState.Added)
        {
            // the same new person signed in twice at once and the other request was faster: the login name is taken now
            _db.Entry(user).State = EntityState.Detached;
            if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == login, cancel))
            {
                return (null, $"The login name “{login}” is already in use.");
            }

            throw;
        }

        await SetRolesAsync(user, roleIds ?? dir.DefaultRoleIds, cancel);
        await _log.InfoAsync(ActivityCategory.Admin, $"User '{login}' was made from the directory '{dir.Name}'.", tenantId: dir.TenantId, userId: user.Id);

        if (createMailbox ?? dir.CreateMailbox)
        {
            await CreateMailboxAsync(dir, entry, user, cancel);
        }

        return (user, null);
    }

    /// <summary>
    /// A local user signs in through the directory from now on: the entry is the same person (the same login name). The password of the
    /// directory counts and the old one never again, not even if the user is let go of the directory later; the open sessions end, so that
    /// the next sign-in is one of the new kind. Returns what is wrong, or null.
    /// </summary>
    public async Task<string?> LinkAsync(DirectoryConnection dir, DirectoryUser entry, User user, CancellationToken cancel = default)
    {
        if (user.DirectoryId is not null)
        {
            return $"“{user.LoginName}” signs in through a directory already.";
        }

        if (user.TenantId != dir.TenantId)
        {
            return $"“{user.LoginName}” belongs to another tenant.";
        }

        Apply(dir, entry, user);
        user.DirectoryId = dir.Id;
        user.DirectoryDisabledDate = null;
        user.MustChangePassword = false;
        user.PasswordHash = _hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        await _db.SaveChangesAsync(cancel);
        await EndSessionsAsync(user, cancel);
        await _log.InfoAsync(ActivityCategory.Admin, $"User '{user.LoginName}' signs in through the directory '{dir.Name}' from now on.", tenantId: dir.TenantId, userId: user.Id);
        return null;
    }

    private async Task SetRolesAsync(User user, long[] wanted, CancellationToken cancel)
    {
        long[] roles = wanted.Length == 0
            ? await _db.Roles.IgnoreQueryFilters().Where(r => r.TenantId == user.TenantId && r.Name == TenantService.UserRoleName).Select(r => r.Id).ToArrayAsync(cancel)
            : await _db.Roles.IgnoreQueryFilters().Where(r => r.TenantId == user.TenantId && wanted.Contains(r.Id)).Select(r => r.Id).ToArrayAsync(cancel);
        foreach (long role in roles)
        {
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role });
        }

        await _db.SaveChangesAsync(cancel);
    }

    /// <summary>A personal mailbox, with the address of the directory when it is one of the tenant's domains (else the mailbox has none and an administrator gives it one).</summary>
    private async Task CreateMailboxAsync(DirectoryConnection dir, DirectoryUser entry, User user, CancellationToken cancel)
    {
        Mailbox mailbox = await _mailboxes.CreateMailboxAsync(user.DisplayName, MailboxType.Personal, user.Id, dir.TenantId);
        string? address = entry.Email?.Trim().ToLowerInvariant();
        if (address is null || !MailAddresses.IsValid(address))
        {
            return;
        }

        string domain = address[(address.LastIndexOf('@') + 1)..];
        if (!await _db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == dir.TenantId && d.Name == domain, cancel))
        {
            await _log.InfoAsync(ActivityCategory.Admin, $"The mailbox of '{user.LoginName}' has no address: {domain} is not a domain of the tenant.", tenantId: dir.TenantId, userId: user.Id);
            return;
        }

        string? error = await _mailboxes.AddAddressAsync(mailbox, address, isPrimary: true);
        if (error is not null)
        {
            await _log.WarnAsync(ActivityCategory.Admin, $"The mailbox of '{user.LoginName}' has no address: {error}", tenantId: dir.TenantId, userId: user.Id);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Keeping in step
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Takes over what the directory says: where the entry is, who it is, the fields of the mapper that have a value. Returns whether anything changed.</summary>
    public static bool Apply(DirectoryConnection dir, DirectoryUser entry, User user)
    {
        bool changed = false;

        void Set<T>(T current, T value, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                assign(value);
                changed = true;
            }
        }

        Set(user.DirectoryDn, entry.Dn, v => user.DirectoryDn = v);
        Set(user.DirectoryUid, entry.Uid, v => user.DirectoryUid = v);
        Set(user.DisplayName, entry.DisplayName.Trim(), v => user.DisplayName = v);

        // What the mapper has no attribute for, or the directory has no value for, stays as an administrator made it.
        if (!string.IsNullOrWhiteSpace(dir.EmailAttribute) && entry.Email is { } email)
        {
            Set(user.Email, email, v => user.Email = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.FirstNameAttribute) && entry.FirstName is { } first)
        {
            Set(user.FirstName, first, v => user.FirstName = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.LastNameAttribute) && entry.LastName is { } last)
        {
            Set(user.LastName, last, v => user.LastName = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.JobTitleAttribute) && entry.JobTitle is { } job)
        {
            Set(user.JobTitle, job, v => user.JobTitle = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.PhoneAttribute) && entry.Phone is { } phone)
        {
            Set(user.Phone, phone, v => user.Phone = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.MobileAttribute) && entry.Mobile is { } mobile)
        {
            Set(user.Mobile, mobile, v => user.Mobile = v);
        }

        if (!string.IsNullOrWhiteSpace(dir.DepartmentAttribute) && entry.Department is { } department)
        {
            Set(user.Department, department, v => user.Department = v);
        }

        return changed;
    }

    /// <summary>
    /// Compares the users of a directory with it: gone, disabled or no longer in the group = cannot sign in (and the open sessions end);
    /// back again = can; the rest is kept in step. A directory that cannot be reached changes nothing.
    /// </summary>
    public async Task<DirectorySyncResult> SyncAsync(DirectoryConnection dir, CancellationToken cancel = default)
    {
        List<User> users = await _db.Users.IgnoreQueryFilters().Where(u => u.DirectoryId == dir.Id).ToListAsync(cancel);
        int updated = 0, blocked = 0, unblocked = 0;
        var notes = new List<string>();
        try
        {
            foreach (User user in users)
            {
                cancel.ThrowIfCancellationRequested();
                DirectoryUser? entry = !string.IsNullOrWhiteSpace(user.DirectoryDn) ? await _directories.ReadUserAsync(dir, user.DirectoryDn, cancel) : null;
                entry ??= (await _directories.FindUserAsync(dir, user.LoginName, cancel)).User;

                bool allowed = entry is { Disabled: false };
                if (!allowed)
                {
                    if (user.DirectoryDisabledDate is null)
                    {
                        user.DirectoryDisabledDate = DateTime.UtcNow;
                        blocked++;
                        notes.Add($"{user.LoginName} cannot sign in any more.");
                        await EndSessionsAsync(user, cancel);
                        await _log.WarnAsync(ActivityCategory.Admin, $"User '{user.LoginName}' was blocked: the directory '{dir.Name}' has them gone, disabled or outside the group.", tenantId: user.TenantId, userId: user.Id);
                    }

                    continue;
                }

                if (user.DirectoryDisabledDate is not null)
                {
                    user.DirectoryDisabledDate = null;
                    unblocked++;
                    notes.Add($"{user.LoginName} can sign in again.");
                }

                if (Apply(dir, entry!, user))
                {
                    updated++;
                }
            }

            dir.LastSyncDate = DateTime.UtcNow;
            dir.LastSyncOk = true;
            dir.LastSyncMessage = $"{users.Count} users compared: {updated} updated, {blocked} blocked, {unblocked} let in again.";
            await _db.SaveChangesAsync(cancel);
            return new DirectorySyncResult(true, dir.LastSyncMessage, users.Count, updated, blocked, unblocked, notes);
        }
        catch (DirectoryException ex)
        {
            // what was blocked or changed before the directory stopped answering stays as it is: nothing is guessed
            dir.LastSyncDate = DateTime.UtcNow;
            dir.LastSyncOk = false;
            dir.LastSyncMessage = "Not compared: " + ex.Message;
            await _db.SaveChangesAsync(cancel);
            return new DirectorySyncResult(false, dir.LastSyncMessage, users.Count, 0, 0, 0, notes);
        }
    }

    /// <summary>The sessions of a user that may not sign in any more end now, not when their cookie runs out.</summary>
    private async Task EndSessionsAsync(User user, CancellationToken cancel)
    {
        await _db.UserSessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(cancel);
        _sessions.InvalidateUser(user.Id);
        _cache.Forget(user.Id);
    }
}

public sealed record DirectorySyncResult(bool Ok, string Message, int Users, int Updated, int Blocked, int UnblockedAgain, IReadOnlyList<string> Notes);

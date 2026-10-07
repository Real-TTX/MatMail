using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>A user who signed in through IMAP or SMTP (or the web) with what they may do.</summary>
public sealed record MailUser(
    long UserId,
    long TenantId,
    string LoginName,
    string DisplayName,
    bool IsSystemAdmin,
    IReadOnlySet<string> Permissions)
{
    public bool Can(string permission) => IsSystemAdmin || Permissions.Contains(permission);
}

/// <summary>A mailbox a user can work in and how far.</summary>
public sealed record AccessibleMailbox(Mailbox Mailbox, MailboxAccess Access, bool IsOwn);

/// <summary>An address a user may send as.</summary>
public sealed record SendIdentity(MailboxAlias Alias, Mailbox Mailbox, MailboxAccess Access, bool IsOwn);

/// <summary>
/// Who may do what with mail: authentication for the protocol servers, the mailboxes a user can reach (own, delegated, shared,
/// "Unassigned" for administrators) and the addresses a user may send as.
/// </summary>
public sealed class MailAccessService
{
    private readonly MatMailDbContext _db;
    private readonly SignInService _signIn;
    private readonly CurrentUser _current;

    public MailAccessService(MatMailDbContext db, SignInService signIn, CurrentUser current)
    {
        _db = db;
        _signIn = signIn;
        _current = current;
    }

    /// <summary>
    /// Checks a login for IMAP/SMTP: password, active user/tenant, and the permission to use mail. On success the current scope
    /// acts as that user (tenant filter, audit columns).
    /// </summary>
    public async Task<MailUser?> AuthenticateAsync(string? loginName, string? password, string? remoteIp)
    {
        SignInOutcome outcome = await _signIn.ValidateCredentialsAsync(loginName, password, remoteIp);
        if (!outcome.Succeeded || outcome.User is null)
        {
            return null;
        }

        MailUser user = await LoadUserAsync(outcome.User);
        if (!user.Can(Permissions.MailUse))
        {
            return null;
        }

        Apply(user);
        return user;
    }

    /// <summary>Makes the current scope act as this user.</summary>
    public void Apply(MailUser user)
        => _current.RunAs(user.UserId, user.TenantId, user.IsSystemAdmin, user.Permissions, user.DisplayName);

    private async Task<MailUser> LoadUserAsync(User user)
    {
        List<string[]> permissions = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role!.Permissions)
            .ToListAsync();
        return new MailUser(
            user.Id,
            user.TenantId,
            user.LoginName,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.LoginName : user.DisplayName,
            user.IsSystemAdmin,
            permissions.SelectMany(p => p).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// All mailboxes the user can open: their personal one (full access), those delegated to them (shared mailboxes and other
    /// people's), and — with the permission — the tenant's "Unassigned" mailbox. Inactive mailboxes are left out.
    /// </summary>
    public async Task<IReadOnlyList<AccessibleMailbox>> GetMailboxesAsync(MailUser user, CancellationToken cancel = default)
    {
        var result = new List<AccessibleMailbox>();

        List<Mailbox> own = await _db.Mailboxes.AsNoTracking()
            .Where(m => m.TenantId == user.TenantId && m.OwnerUserId == user.UserId && m.IsActive).OrderBy(m => m.Name).ToListAsync(cancel);
        result.AddRange(own.Select(m => new AccessibleMailbox(m, MailboxAccess.Manage, true)));

        var delegated = await _db.MailboxPermissions.AsNoTracking()
            .Where(p => p.UserId == user.UserId && p.Mailbox!.IsActive && p.Mailbox.TenantId == user.TenantId)
            .Select(p => new { p.Mailbox, p.Access })
            .ToListAsync(cancel);
        result.AddRange(delegated
            .Where(d => d.Mailbox!.OwnerUserId != user.UserId)
            .OrderBy(d => d.Mailbox!.Type).ThenBy(d => d.Mailbox!.Name)
            .Select(d => new AccessibleMailbox(d.Mailbox!, d.Access, false)));

        if (user.Can(Permissions.UnassignedManage) && result.All(r => r.Mailbox.Type != MailboxType.Unassigned))
        {
            Mailbox? unassigned = await _db.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.TenantId == user.TenantId && m.Type == MailboxType.Unassigned, cancel);
            if (unassigned is not null)
            {
                result.Add(new AccessibleMailbox(unassigned, MailboxAccess.Edit, false));
            }
        }

        return result;
    }

    /// <summary>The user's access to one mailbox (null = none).</summary>
    public async Task<MailboxAccess?> GetAccessAsync(MailUser user, long mailboxId, CancellationToken cancel = default)
    {
        AccessibleMailbox? hit = (await GetMailboxesAsync(user, cancel)).FirstOrDefault(m => m.Mailbox.Id == mailboxId);
        return hit?.Access;
    }

    /// <summary>The mailbox of a folder when the user may reach it with at least <paramref name="minimum"/>; otherwise null.</summary>
    public async Task<MailFolder?> GetFolderAsync(MailUser user, long folderId, MailboxAccess minimum, CancellationToken cancel = default)
    {
        MailFolder? folder = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId && f.TenantId == user.TenantId, cancel);
        if (folder is null)
        {
            return null;
        }

        MailboxAccess? access = await GetAccessAsync(user, folder.MailboxId, cancel);
        return access >= minimum ? folder : null;
    }

    /// <summary>
    /// The address a user may use as sender: their own mailbox' addresses and those of mailboxes where they hold "send" rights.
    /// Returns null when the address is unknown or not allowed.
    /// </summary>
    public async Task<SendIdentity?> FindSendIdentityAsync(MailUser user, string address, CancellationToken cancel = default)
    {
        string normalized = MailAddresses.Normalize(address);
        MailboxAlias? alias = await _db.MailboxAliases.AsNoTracking().Include(a => a.Mailbox)
            .FirstOrDefaultAsync(a => a.Address == normalized && a.TenantId == user.TenantId && a.CanSend, cancel);
        if (alias?.Mailbox is null || !alias.Mailbox.IsActive)
        {
            return null;
        }

        MailboxAccess? access = await GetAccessAsync(user, alias.MailboxId, cancel);
        bool own = alias.Mailbox.OwnerUserId == user.UserId;
        return access >= MailboxAccess.Send || own ? new SendIdentity(alias, alias.Mailbox, access ?? MailboxAccess.Manage, own) : null;
    }

    /// <summary>Every address the user may send as (for the "From" list of the web client).</summary>
    public async Task<IReadOnlyList<SendIdentity>> GetSendIdentitiesAsync(MailUser user, CancellationToken cancel = default)
    {
        IReadOnlyList<AccessibleMailbox> mailboxes = await GetMailboxesAsync(user, cancel);
        long[] ids = mailboxes.Where(m => m.Access >= MailboxAccess.Send).Select(m => m.Mailbox.Id).ToArray();
        List<MailboxAlias> aliases = await _db.MailboxAliases.AsNoTracking()
            .Where(a => ids.Contains(a.MailboxId) && a.CanSend)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address)
            .ToListAsync(cancel);

        return aliases
            .Select(a =>
            {
                AccessibleMailbox box = mailboxes.First(m => m.Mailbox.Id == a.MailboxId);
                return new SendIdentity(a, box.Mailbox, box.Access, box.IsOwn);
            })
            .OrderByDescending(i => i.IsOwn)
            .ThenByDescending(i => i.Alias.IsPrimary)
            .ThenBy(i => i.Alias.Address)
            .ToList();
    }
}

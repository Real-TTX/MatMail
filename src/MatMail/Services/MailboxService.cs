using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// Creating and shaping mailboxes: the default folders, addresses (aliases), the tenant's "Unassigned" bucket and who
/// may work in a mailbox. Message handling lives in <see cref="MailStore"/>.
/// </summary>
public sealed class MailboxService
{
    /// <summary>The folders every mailbox starts with. Names are IMAP-style; the web client shows localized labels by kind.</summary>
    public static readonly IReadOnlyList<(string Name, FolderKind Kind)> DefaultFolders = new[]
    {
        ("INBOX", FolderKind.Inbox),
        ("Drafts", FolderKind.Drafts),
        ("Sent", FolderKind.Sent),
        ("Junk", FolderKind.Junk),
        ("Trash", FolderKind.Trash),
        ("Archive", FolderKind.Archive),
    };

    private readonly MatMailDbContext _db;
    private readonly CurrentUser _current;

    public MailboxService(MatMailDbContext db, CurrentUser current)
    {
        _db = db;
        _current = current;
    }

    /// <summary>Creates a mailbox with its default folders (and optionally its primary address). Saves.</summary>
    public async Task<Mailbox> CreateMailboxAsync(string name, MailboxType type, long? ownerUserId, long? tenantId = null)
    {
        long tenant = tenantId ?? _current.TenantId ?? throw new InvalidOperationException("No tenant to create the mailbox in.");
        var mailbox = new Mailbox { TenantId = tenant, Name = name.Trim(), Type = type, OwnerUserId = ownerUserId };
        _db.Mailboxes.Add(mailbox);
        await _db.SaveChangesAsync();

        await EnsureDefaultFoldersAsync(mailbox);
        return mailbox;
    }

    /// <summary>Adds the folders a mailbox is missing. Saves.</summary>
    public async Task EnsureDefaultFoldersAsync(Mailbox mailbox)
    {
        HashSet<FolderKind> existing = (await _db.MailFolders
                .Where(f => f.MailboxId == mailbox.Id && f.Kind != FolderKind.Custom)
                .Select(f => f.Kind)
                .ToListAsync())
            .ToHashSet();

        long validity = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach ((string folderName, FolderKind kind) in DefaultFolders)
        {
            if (existing.Contains(kind))
            {
                continue;
            }

            _db.MailFolders.Add(new MailFolder
            {
                TenantId = mailbox.TenantId,
                MailboxId = mailbox.Id,
                Name = folderName,
                Kind = kind,
                UidValidity = validity++,
                UidNext = 1,
            });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>The tenant's "Unassigned" mailbox (created on demand): where mail nobody could be found for ends up.</summary>
    public async Task<Mailbox> GetUnassignedMailboxAsync(long tenantId)
    {
        Mailbox? mailbox = await _db.Mailboxes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Type == MailboxType.Unassigned);
        if (mailbox is not null)
        {
            return mailbox;
        }

        return await CreateMailboxAsync("Unassigned", MailboxType.Unassigned, null, tenantId);
    }

    /// <summary>
    /// Adds an address to a mailbox. Returns an error text (English source string) or null on success.
    /// The domain must be registered for the tenant; with <paramref name="registerDomain"/> it is registered on the fly.
    /// </summary>
    public async Task<string?> AddAddressAsync(Mailbox mailbox, string address, bool isPrimary, bool canSend = true, long? sendAccountId = null, bool registerDomain = false)
    {
        string normalized = MailAddresses.Normalize(address);
        if (!MailAddresses.TrySplit(normalized, out _, out string domain))
        {
            return "The e-mail address is not valid.";
        }

        Domain? domainRow = await _db.Domains.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Name == domain);
        if (domainRow is null)
        {
            if (!registerDomain)
            {
                return "The domain of this address is not registered. Add it under Domains first.";
            }

            _db.Domains.Add(new Domain { TenantId = mailbox.TenantId, Name = domain });
        }
        else if (domainRow.TenantId != mailbox.TenantId)
        {
            return "The domain of this address belongs to another tenant.";
        }

        if (await _db.MailboxAliases.IgnoreQueryFilters().AnyAsync(a => a.Address == normalized))
        {
            return "This address is already in use.";
        }

        if (isPrimary)
        {
            List<MailboxAlias> others = await _db.MailboxAliases.Where(a => a.MailboxId == mailbox.Id && a.IsPrimary).ToListAsync();
            others.ForEach(a => a.IsPrimary = false);
        }

        _db.MailboxAliases.Add(new MailboxAlias
        {
            TenantId = mailbox.TenantId,
            MailboxId = mailbox.Id,
            Address = normalized,
            IsPrimary = isPrimary,
            CanSend = canSend && !MailAddresses.IsCatchAll(normalized),
            SendAccountId = sendAccountId,
        });
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>What a user may do in a mailbox: the owner manages it, otherwise the delegated level (null = no access).</summary>
    public async Task<MailboxAccess?> GetAccessAsync(long userId, long mailboxId)
    {
        Mailbox? mailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailboxId && m.IsActive);
        if (mailbox is null)
        {
            return null;
        }

        if (mailbox.OwnerUserId == userId)
        {
            return MailboxAccess.Manage;
        }

        MailboxAccess? delegated = await _db.MailboxPermissions.AsNoTracking()
            .Where(p => p.MailboxId == mailboxId && p.UserId == userId)
            .Select(p => (MailboxAccess?)p.Access)
            .FirstOrDefaultAsync();

        if (mailbox.Type == MailboxType.Unassigned && _current.Can(Permissions.UnassignedManage))
        {
            return delegated is MailboxAccess d && d > MailboxAccess.Edit ? d : MailboxAccess.Edit;
        }

        return delegated;
    }
}

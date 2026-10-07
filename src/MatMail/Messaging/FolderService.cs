using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>A folder together with its full path ("Archive/2025/Bills"), as IMAP shows it.</summary>
public sealed record FolderInfo(MailFolder Folder, string Path)
{
    public long Id => Folder.Id;
    public FolderKind Kind => Folder.Kind;
}

/// <summary>Folders of a mailbox: listing with paths, creating, renaming, deleting, subscriptions.</summary>
public sealed class FolderService
{
    public const char Separator = '/';

    private readonly MatMailDbContext _db;
    private readonly MailEventHub _hub;

    public FolderService(MatMailDbContext db, MailEventHub hub)
    {
        _db = db;
        _hub = hub;
    }

    /// <summary>All folders of a mailbox with their paths: special folders first (Inbox, Drafts, Sent, ...), then by path.</summary>
    public async Task<IReadOnlyList<FolderInfo>> ListAsync(long mailboxId, CancellationToken cancel = default)
    {
        List<MailFolder> folders = await _db.MailFolders.AsNoTracking().Where(f => f.MailboxId == mailboxId).ToListAsync(cancel);
        Dictionary<long, MailFolder> byId = folders.ToDictionary(f => f.Id);

        string PathOf(MailFolder folder)
        {
            var parts = new Stack<string>();
            for (MailFolder? current = folder; current is not null; current = current.ParentId is long parent && byId.TryGetValue(parent, out MailFolder? p) ? p : null)
            {
                parts.Push(current.Name);
            }

            return string.Join(Separator, parts);
        }

        return folders
            .Select(f => new FolderInfo(f, PathOf(f)))
            .OrderBy(i => SortRank(i.Folder))
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<MailFolder?> FindByKindAsync(long mailboxId, FolderKind kind, CancellationToken cancel = default)
        => _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.MailboxId == mailboxId && f.Kind == kind, cancel);

    /// <summary>The folder with this path ("INBOX", "Archive/2025"; case-insensitive), or null.</summary>
    public async Task<FolderInfo?> FindByPathAsync(long mailboxId, string path, CancellationToken cancel = default)
    {
        string wanted = path.Trim(Separator);
        IReadOnlyList<FolderInfo> all = await ListAsync(mailboxId, cancel);
        return all.FirstOrDefault(f => string.Equals(f.Path, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates the folder (and missing parents) for a path like "Archive/2025". Returns the folder, or an error text.</summary>
    public async Task<(MailFolder? Folder, string? Error)> CreateAsync(long mailboxId, string path, CancellationToken cancel = default)
    {
        string[] parts = path.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length > 200 || p.Contains('\\') || p is "." or ".."))
        {
            return (null, "The folder name is not valid.");
        }

        Mailbox? mailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailboxId, cancel);
        if (mailbox is null)
        {
            return (null, "The mailbox does not exist.");
        }

        MailFolder? parent = null;
        MailFolder? created = null;
        for (int i = 0; i < parts.Length; i++)
        {
            long? parentId = parent?.Id;
            string name = parts[i];
            MailFolder? existing = await _db.MailFolders.FirstOrDefaultAsync(f => f.MailboxId == mailboxId && f.ParentId == parentId && f.Name.ToLower() == name.ToLower(), cancel);
            if (existing is not null)
            {
                if (i == parts.Length - 1)
                {
                    return (null, "A folder with this name already exists.");
                }

                parent = existing;
                continue;
            }

            if (parent is null && IsReservedRootName(name))
            {
                return (null, "This name is reserved for a system folder.");
            }

            created = new MailFolder
            {
                TenantId = mailbox.TenantId,
                MailboxId = mailboxId,
                ParentId = parentId,
                Name = name,
                Kind = FolderKind.Custom,
                UidValidity = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                UidNext = 1,
            };
            _db.MailFolders.Add(created);
            await _db.SaveChangesAsync(cancel);
            parent = created;
        }

        _hub.Publish(new MailEvent(MailEventKind.FoldersChanged, mailbox.TenantId, mailboxId, created?.Id ?? 0));
        return (created, null);
    }

    /// <summary>Renames a custom folder and/or moves it below another parent (new path). Returns an error text or null.</summary>
    public async Task<string?> RenameAsync(long folderId, string newPath, CancellationToken cancel = default)
    {
        MailFolder? folder = await _db.MailFolders.FirstOrDefaultAsync(f => f.Id == folderId, cancel);
        if (folder is null)
        {
            return "The folder does not exist.";
        }

        if (folder.Kind != FolderKind.Custom)
        {
            return "System folders cannot be renamed.";
        }

        string[] parts = newPath.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "The folder name is not valid.";
        }

        long? parentId = null;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string name = parts[i];
            long? currentParent = parentId;
            MailFolder? segment = await _db.MailFolders.FirstOrDefaultAsync(f => f.MailboxId == folder.MailboxId && f.ParentId == currentParent && f.Name.ToLower() == name.ToLower(), cancel);
            if (segment is null)
            {
                (MailFolder? made, string? error) = await CreateAsync(folder.MailboxId, string.Join(Separator, parts[..(i + 1)]), cancel);
                if (made is null)
                {
                    return error;
                }

                segment = made;
            }

            if (segment.Id == folderId)
            {
                return "A folder cannot be moved into itself.";
            }

            parentId = segment.Id;
        }

        string leaf = parts[^1];
        long? finalParent = parentId;
        if (await _db.MailFolders.AnyAsync(f => f.Id != folderId && f.MailboxId == folder.MailboxId && f.ParentId == finalParent && f.Name.ToLower() == leaf.ToLower(), cancel))
        {
            return "A folder with this name already exists.";
        }

        folder.Name = leaf;
        folder.ParentId = parentId;
        await _db.SaveChangesAsync(cancel);
        _hub.Publish(new MailEvent(MailEventKind.FoldersChanged, folder.TenantId, folder.MailboxId, folderId));
        return null;
    }

    /// <summary>Deletes a custom folder with its subfolders and messages. Returns an error text or null.</summary>
    public async Task<string?> DeleteAsync(long folderId, CancellationToken cancel = default)
    {
        MailFolder? folder = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId, cancel);
        if (folder is null)
        {
            return "The folder does not exist.";
        }

        if (folder.Kind != FolderKind.Custom)
        {
            return "System folders cannot be deleted.";
        }

        // Subfolders and their messages are removed by the cascading foreign keys.
        await _db.MailFolders.Where(f => f.Id == folderId).ExecuteDeleteAsync(cancel);
        _hub.Publish(new MailEvent(MailEventKind.FoldersChanged, folder.TenantId, folder.MailboxId, folderId));
        return null;
    }

    public async Task SetSubscribedAsync(long folderId, bool subscribed, CancellationToken cancel = default)
        => await _db.MailFolders.Where(f => f.Id == folderId).ExecuteUpdateAsync(s => s.SetProperty(f => f.IsSubscribed, subscribed), cancel);

    /// <summary>Finds or creates a custom folder by path (used by migration and import, which mirror a provider's folder tree).</summary>
    public async Task<MailFolder> EnsureAsync(long mailboxId, string path, CancellationToken cancel = default)
    {
        FolderInfo? existing = await FindByPathAsync(mailboxId, path, cancel);
        if (existing is not null)
        {
            return existing.Folder;
        }

        (MailFolder? created, string? error) = await CreateAsync(mailboxId, path, cancel);
        if (created is not null)
        {
            return created;
        }

        // Created by someone else in the meantime, or a name clash with a system folder: look again.
        existing = await FindByPathAsync(mailboxId, path, cancel);
        return existing?.Folder ?? throw new InvalidOperationException($"Folder '{path}' could not be created: {error}");
    }

    private static int SortRank(MailFolder folder) => folder.Kind switch
    {
        FolderKind.Inbox => 0,
        FolderKind.Drafts => 1,
        FolderKind.Sent => 2,
        FolderKind.Archive => 3,
        FolderKind.Junk => 4,
        FolderKind.Trash => 5,
        _ => 10,
    };

    private static bool IsReservedRootName(string name)
        => name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
           || MailboxService.DefaultFolders.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

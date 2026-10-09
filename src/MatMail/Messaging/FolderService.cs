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

    /// <summary>The deepest nesting of folders: deeper trees are unusable in every client and a cycle would never end.</summary>
    public const int MaxDepth = 10;

    /// <summary>
    /// All folders of a mailbox with their paths as a tree, depth first: the special folders at the top level first (Inbox, Drafts, Sent, ...),
    /// then the others by name; every folder is directly followed by its subfolders.
    /// </summary>
    public async Task<IReadOnlyList<FolderInfo>> ListAsync(long mailboxId, CancellationToken cancel = default)
    {
        List<MailFolder> folders = await _db.MailFolders.AsNoTracking().Where(f => f.MailboxId == mailboxId).ToListAsync(cancel);
        return Arrange(folders);
    }

    /// <summary>Puts folders into tree order and gives each its path. Folders whose parent is missing are shown at the top level.</summary>
    public static IReadOnlyList<FolderInfo> Arrange(IReadOnlyCollection<MailFolder> folders)
    {
        Dictionary<long, MailFolder> byId = folders.ToDictionary(f => f.Id);
        ILookup<long, MailFolder> children = folders
            .Where(f => f.ParentId is long parent && byId.ContainsKey(parent))
            .ToLookup(f => f.ParentId!.Value);
        IEnumerable<MailFolder> roots = folders
            .Where(f => f.ParentId is not long parent || !byId.ContainsKey(parent))
            .OrderBy(SortRank)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<FolderInfo>(folders.Count);
        var visited = new HashSet<long>();

        void Walk(MailFolder folder, string parentPath)
        {
            if (!visited.Add(folder.Id))
            {
                return;
            }

            string path = parentPath.Length == 0 ? folder.Name : parentPath + Separator + folder.Name;
            result.Add(new FolderInfo(folder, path));
            foreach (MailFolder child in children[folder.Id].OrderBy(SortRank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                Walk(child, path);
            }
        }

        foreach (MailFolder root in roots)
        {
            Walk(root, string.Empty);
        }

        return result;
    }

    /// <summary>The folder and everything below it.</summary>
    public async Task<IReadOnlyList<MailFolder>> GetSubtreeAsync(long folderId, CancellationToken cancel = default)
    {
        MailFolder? start = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId, cancel);
        if (start is null)
        {
            return Array.Empty<MailFolder>();
        }

        List<MailFolder> all = await _db.MailFolders.AsNoTracking().Where(f => f.MailboxId == start.MailboxId).ToListAsync(cancel);
        ILookup<long, MailFolder> children = all.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value);
        var result = new List<MailFolder>();
        var queue = new Queue<MailFolder>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            MailFolder current = queue.Dequeue();
            if (result.Any(r => r.Id == current.Id))
            {
                continue;
            }

            result.Add(current);
            foreach (MailFolder child in children[current.Id])
            {
                queue.Enqueue(child);
            }
        }

        return result;
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

        if (parts.Length > MaxDepth)
        {
            return (null, "The folders are nested too deeply.");
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

    /// <summary>
    /// Creates a folder below another one ("New subfolder"); <paramref name="parentId"/> null puts it at the top level. The name may itself
    /// be a path ("2025/Bills"). Returns the folder, or an error text.
    /// </summary>
    public async Task<(MailFolder? Folder, string? Error)> CreateUnderAsync(long mailboxId, long? parentId, string name, CancellationToken cancel = default)
    {
        if (parentId is not long parent)
        {
            return await CreateAsync(mailboxId, name, cancel);
        }

        FolderInfo? parentInfo = (await ListAsync(mailboxId, cancel)).FirstOrDefault(f => f.Id == parent);
        return parentInfo is null
            ? (null, "The folder does not exist.")
            : await CreateAsync(mailboxId, parentInfo.Path + Separator + name.Trim(Separator, ' '), cancel);
    }

    /// <summary>
    /// Moves a custom folder with everything below it below another folder of the same mailbox, or to the top level
    /// (<paramref name="newParentId"/> null). Returns an error text or null.
    /// </summary>
    public async Task<string?> MoveAsync(long folderId, long? newParentId, CancellationToken cancel = default)
    {
        MailFolder? folder = await _db.MailFolders.FirstOrDefaultAsync(f => f.Id == folderId, cancel);
        if (folder is null)
        {
            return "The folder does not exist.";
        }

        if (folder.Kind != FolderKind.Custom)
        {
            return "System folders cannot be moved.";
        }

        if (folder.ParentId == newParentId)
        {
            return null;
        }

        List<MailFolder> all = await _db.MailFolders.AsNoTracking().Where(f => f.MailboxId == folder.MailboxId).ToListAsync(cancel);
        Dictionary<long, MailFolder> byId = all.ToDictionary(f => f.Id);
        MailFolder? parent = null;
        if (newParentId is long parentId && !byId.TryGetValue(parentId, out parent))
        {
            return "The folder does not exist.";
        }

        ILookup<long, MailFolder> children = all.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value);
        int height = HeightOf(folderId, children, out HashSet<long> subtree);
        if (parent is not null && subtree.Contains(parent.Id))
        {
            return "A folder cannot be moved into itself or one of its subfolders.";
        }

        if (DepthOf(parent, byId) + height > MaxDepth)
        {
            return "The folders are nested too deeply.";
        }

        if (parent is null && IsReservedRootName(folder.Name))
        {
            return "This name is reserved for a system folder.";
        }

        if (all.Any(f => f.Id != folderId && f.ParentId == newParentId && f.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return "A folder with this name already exists.";
        }

        folder.ParentId = newParentId;
        await _db.SaveChangesAsync(cancel);
        _hub.Publish(new MailEvent(MailEventKind.FoldersChanged, folder.TenantId, folder.MailboxId, folderId));
        return null;
    }

    /// <summary>How many levels there are from the folder down (itself counts), and the ids of the folder and everything below it.</summary>
    private static int HeightOf(long folderId, ILookup<long, MailFolder> children, out HashSet<long> subtree)
    {
        subtree = new HashSet<long> { folderId };
        int height = 1;
        var level = new List<long> { folderId };
        while (level.Count > 0)
        {
            level = level.SelectMany(id => children[id]).Select(c => c.Id).Where(subtree.Add).ToList();
            if (level.Count > 0)
            {
                height++;
            }
        }

        return height;
    }

    /// <summary>The number of folders from the top level down to and including <paramref name="folder"/> (0 for the top level itself).</summary>
    private static int DepthOf(MailFolder? folder, Dictionary<long, MailFolder> byId)
    {
        int depth = 0;
        for (MailFolder? current = folder; current is not null && depth <= MaxDepth; current = current.ParentId is long p && byId.TryGetValue(p, out MailFolder? up) ? up : null)
        {
            depth++;
        }

        return depth;
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

        List<MailFolder> inMailbox = await _db.MailFolders.AsNoTracking().Where(f => f.MailboxId == folder.MailboxId).ToListAsync(cancel);
        int levelsBelow = HeightOf(folderId, inMailbox.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value), out _) - 1;
        if (parts.Length + levelsBelow > MaxDepth)
        {
            return "The folders are nested too deeply.";
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

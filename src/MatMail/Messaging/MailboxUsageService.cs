using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>What a mailbox holds: messages, the bytes stored here, and the bytes that stay at the provider (live access).</summary>
public sealed record MailboxUsage(long Messages, long LocalBytes, long RemoteBytes)
{
    public static readonly MailboxUsage Empty = new(0, 0, 0);
}

/// <summary>What one folder holds.</summary>
public sealed record FolderUsage(long FolderId, string Path, FolderKind Kind, long Messages, long Bytes);

/// <summary>
/// The storage a mailbox occupies: the sizes of its messages (the complete messages as received, attachments included). Live-access
/// messages only stand in for a message at the provider and are counted apart, so they do not count as space used here.
/// </summary>
public sealed class MailboxUsageService
{
    private readonly MatMailDbContext _db;
    private readonly FolderService _folders;

    public MailboxUsageService(MatMailDbContext db, FolderService folders)
    {
        _db = db;
        _folders = folders;
    }

    /// <summary>The usage of several mailboxes with one query; a mailbox without messages is there with zeros.</summary>
    public async Task<Dictionary<long, MailboxUsage>> GetAsync(IEnumerable<long> mailboxIds, CancellationToken cancel = default)
    {
        long[] ids = mailboxIds.Distinct().ToArray();
        // Looked up by mailbox, not by tenant: a message to a mailbox of another tenant has to see how full that mailbox is.
        var rows = await _db.MailMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => ids.Contains(m.MailboxId))
            .GroupBy(m => m.MailboxId)
            .Select(g => new
            {
                MailboxId = g.Key,
                Messages = g.LongCount(),
                Local = g.Sum(m => m.Storage == MessageStorage.Local ? m.SizeBytes : 0),
                Remote = g.Sum(m => m.Storage == MessageStorage.Remote ? m.SizeBytes : 0),
            })
            .ToListAsync(cancel);

        Dictionary<long, MailboxUsage> found = rows.ToDictionary(r => r.MailboxId, r => new MailboxUsage(r.Messages, r.Local, r.Remote));
        return ids.ToDictionary(id => id, id => found.GetValueOrDefault(id, MailboxUsage.Empty));
    }

    public async Task<MailboxUsage> GetAsync(long mailboxId, CancellationToken cancel = default)
        => (await GetAsync(new[] { mailboxId }, cancel))[mailboxId];

    /// <summary>The folders of a mailbox in tree order with what each holds (its own messages, not those of subfolders).</summary>
    public async Task<IReadOnlyList<FolderUsage>> GetFoldersAsync(long mailboxId, CancellationToken cancel = default)
    {
        IReadOnlyList<FolderInfo> folders = await _folders.ListAsync(mailboxId, cancel);
        var rows = await _db.MailMessages.AsNoTracking()
            .Where(m => m.MailboxId == mailboxId && m.Storage == MessageStorage.Local)
            .GroupBy(m => m.FolderId)
            .Select(g => new { FolderId = g.Key, Messages = g.LongCount(), Bytes = g.Sum(m => m.SizeBytes) })
            .ToListAsync(cancel);
        Dictionary<long, (long Messages, long Bytes)> byFolder = rows.ToDictionary(r => r.FolderId, r => (r.Messages, r.Bytes));
        return folders
            .Select(f => byFolder.TryGetValue(f.Id, out var usage)
                ? new FolderUsage(f.Id, f.Path, f.Kind, usage.Messages, usage.Bytes)
                : new FolderUsage(f.Id, f.Path, f.Kind, 0, 0))
            .ToList();
    }
}

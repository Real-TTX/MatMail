using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>One message of a list, as much as the list shows of it.</summary>
public sealed record MessageListRow(
    long Id, long FolderId, long Uid, string Subject, string FromName, string FromAddress, string ToSummary, string Preview, DateTime ReceivedDate,
    bool IsRead, bool IsStarred, bool HasAttachments, bool IsDraft, bool IsAnswered, bool IsForwarded, FolderKind FolderKind);

/// <summary>
/// One page of a list: single messages, or conversations (<see cref="Conversations"/>) when the list of a folder is shown in conversation view.
/// <see cref="Size"/> is the page size after clamping.
/// </summary>
public sealed record MessageListPage(int Total, int Page, int Size, IReadOnlyList<MessageListRow> Messages, ConversationPage? Conversations);

/// <summary>Every message a list request matches (the newest ones when there are very many), with the folder of each.</summary>
public sealed record MatchingMessageIds(int Total, long[] Ids, long[] FolderIds, bool Capped);

/// <summary>
/// The lists the web client shows: a folder or a search in a mailbox, page by page, the ids for "select all that match" and the conversation
/// a message belongs to. Whether the user may see the mailbox is the caller's business (<see cref="MailAccessService"/>).
/// </summary>
public sealed class MailListService(MatMailDbContext db)
{
    /// <summary>The most messages that "select everything that matches" takes along; the newest come first.</summary>
    public const int MaxBulk = 10_000;

    public async Task<MessageListPage> ListAsync(long mailboxId, long? folderId, string? q, int? page, int? pageSize, bool? conversations, CancellationToken cancel = default)
    {
        MailQuery query = MailQuery.Parse(mailboxId, folderId, q);
        await query.ResolveFoldersAsync(db, cancel);
        int size = Math.Clamp(pageSize ?? 50, 10, 100);
        IQueryable<MailMessage> filtered = query.Apply(db.MailMessages.AsNoTracking(), db);

        // Conversations are for the lists of a folder; a search and the drafts (which open for writing, not for reading) stay lists of single messages.
        if (conversations == true && folderId is long conversationFolder && string.IsNullOrWhiteSpace(q)
            && await db.MailFolders.AsNoTracking().AnyAsync(f => f.Id == conversationFolder && f.MailboxId == mailboxId && f.Kind != FolderKind.Drafts, cancel))
        {
            ConversationPage conversationPage = await MailConversations.ListAsync(filtered, page, size, cancel);
            return new MessageListPage(conversationPage.Total, conversationPage.Page, size, Array.Empty<MessageListRow>(), conversationPage);
        }

        int total = await filtered.CountAsync(cancel);
        int pageNumber = Math.Clamp(page ?? 1, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));

        List<MessageListRow> rows = await filtered
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(m => new MessageListRow(
                m.Id, m.FolderId, m.Uid, m.Subject, m.FromName, m.FromAddress, m.ToSummary, m.Preview, m.ReceivedDate,
                m.IsRead, m.IsStarred, m.HasAttachments, m.IsDraft, m.IsAnswered, m.IsForwarded, m.Folder!.Kind))
            .ToListAsync(cancel);
        return new MessageListPage(total, pageNumber, size, rows, null);
    }

    /// <summary>
    /// Every message the list of this request would show over all its pages (the newest <see cref="MaxBulk"/> at most), for "select all that
    /// match": the client then changes them in chunks through the usual calls, which check the rights again.
    /// </summary>
    public async Task<MatchingMessageIds> ListIdsAsync(long mailboxId, long? folderId, string? q, CancellationToken cancel = default)
    {
        MailQuery query = MailQuery.Parse(mailboxId, folderId, q);
        await query.ResolveFoldersAsync(db, cancel);
        IQueryable<MailMessage> filtered = query.Apply(db.MailMessages.AsNoTracking(), db);
        int total = await filtered.CountAsync(cancel);
        var rows = await filtered.Select(m => new { m.Id, m.FolderId }).Take(MaxBulk).ToListAsync(cancel);
        return new MatchingMessageIds(total, rows.Select(r => r.Id).ToArray(), rows.Select(r => r.FolderId).ToArray(), total > rows.Count);
    }

    /// <summary>The messages of the conversation that a message belongs to, oldest first (see <see cref="MailConversations.ThreadAsync"/>).</summary>
    public async Task<IReadOnlyList<ThreadMessage>> GetThreadAsync(MailMessage message, CancellationToken cancel = default)
    {
        FolderKind folderKind = await db.MailFolders.AsNoTracking().Where(f => f.Id == message.FolderId).Select(f => f.Kind).FirstAsync(cancel);
        return await MailConversations.ThreadAsync(db, message, folderKind, cancel);
    }
}

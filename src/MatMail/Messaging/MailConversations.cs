using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>One row of a list of conversations: the newest message of a thread within the list, and what the thread comes to.</summary>
public sealed record ConversationRow(
    long Id, long FolderId, long Uid, string Subject, string FromName, string FromAddress, string ToSummary, string Preview, DateTime ReceivedDate,
    bool IsRead, bool IsStarred, bool HasAttachments, bool IsAnswered, bool IsForwarded, FolderKind FolderKind,
    long[] Ids, int UnreadCount, string[] Participants);

public sealed record ConversationPage(int Total, int Page, IReadOnlyList<ConversationRow> Rows);

/// <summary>A message of a conversation as the reader lists it before any of them is opened.</summary>
public sealed record ThreadMessage(
    long Id, long FolderId, FolderKind FolderKind, string Subject, string FromName, string FromAddress, string ToSummary, string Preview, DateTime ReceivedDate,
    bool IsRead, bool IsStarred, bool HasAttachments);

/// <summary>
/// Conversations: the messages of one thread (<see cref="MailMessage.ThreadKey"/>: the root of the reply chain, else the subject)
/// as one row in the list and as one stack in the reader. A message without a key is a conversation of its own.
/// </summary>
public static class MailConversations
{
    /// <summary>The most messages of one conversation that a row names (for the actions on the whole row).</summary>
    public const int MaxIdsPerRow = 500;

    /// <summary>The most names of people that a row shows.</summary>
    public const int MaxParticipants = 4;

    /// <summary>The most messages the reader stacks.</summary>
    public const int MaxThreadMessages = 200;

    /// <summary>
    /// The conversations of a list: <paramref name="scope"/> is what the list would show as single messages (a folder); every thread in it is one
    /// row, newest conversation first, the page counted in conversations.
    /// </summary>
    public static async Task<ConversationPage> ListAsync(IQueryable<MailMessage> scope, int? page, int size, CancellationToken cancel)
    {
        int total = await scope.Select(m => m.ThreadKey ?? "#" + m.Id).Distinct().CountAsync(cancel);
        int pageNumber = Math.Clamp(page ?? 1, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));

        // The conversations of this page, newest first ...
        var keys = await scope
            .GroupBy(m => m.ThreadKey ?? "#" + m.Id)
            .Select(g => new { Key = g.Key, Last = g.Max(m => m.ReceivedDate), LastId = g.Max(m => m.Id) })
            .OrderByDescending(x => x.Last).ThenByDescending(x => x.LastId)
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancel);
        string[] pageKeys = keys.Select(k => k.Key).ToArray();

        // ... and what is in them.
        var rows = await scope
            .Where(m => pageKeys.Contains(m.ThreadKey ?? "#" + m.Id))
            .Select(m => new
            {
                Key = m.ThreadKey ?? "#" + m.Id, m.Id, m.FolderId, m.Uid, m.Subject, m.FromName, m.FromAddress, m.ToSummary, m.Preview, m.ReceivedDate,
                m.IsRead, m.IsStarred, m.HasAttachments, m.IsAnswered, m.IsForwarded, Kind = m.Folder!.Kind,
            })
            .ToListAsync(cancel);

        var byKey = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReceivedDate).ThenBy(r => r.Id).ToList());
        var result = new List<ConversationRow>();
        foreach (var key in keys)
        {
            if (!byKey.TryGetValue(key.Key, out var thread))
            {
                continue;
            }

            var latest = thread[^1];
            result.Add(new ConversationRow(
                latest.Id, latest.FolderId, latest.Uid, StripReplyPrefixes(latest.Subject), latest.FromName, latest.FromAddress, latest.ToSummary, latest.Preview, latest.ReceivedDate,
                thread.All(r => r.IsRead), thread.Any(r => r.IsStarred), thread.Any(r => r.HasAttachments), latest.IsAnswered, latest.IsForwarded, latest.Kind,
                thread.TakeLast(MaxIdsPerRow).Select(r => r.Id).ToArray(),
                thread.Count(r => !r.IsRead),
                thread.Select(r => string.IsNullOrWhiteSpace(r.FromName) ? r.FromAddress : r.FromName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxParticipants).ToArray()));
        }

        return new ConversationPage(total, pageNumber, result);
    }

    /// <summary>
    /// The messages of the conversation that <paramref name="anchor"/> belongs to, oldest first: those of its mailbox with the same key, drafts
    /// left out. Trash and spam are not part of a conversation – unless the message that is open is itself in one of them, then they are the conversation.
    /// </summary>
    public static async Task<IReadOnlyList<ThreadMessage>> ThreadAsync(MatMailDbContext db, MailMessage anchor, FolderKind anchorFolder, CancellationToken cancel)
    {
        IQueryable<MailMessage> thread = db.MailMessages.AsNoTracking().Where(m => m.MailboxId == anchor.MailboxId && !m.IsDraft);
        thread = anchor.ThreadKey is { } key ? thread.Where(m => m.ThreadKey == key) : thread.Where(m => m.Id == anchor.Id);
        thread = anchorFolder is FolderKind.Trash or FolderKind.Junk
            ? thread.Where(m => m.Folder!.Kind == FolderKind.Trash || m.Folder.Kind == FolderKind.Junk)
            : thread.Where(m => m.Folder!.Kind != FolderKind.Trash && m.Folder.Kind != FolderKind.Junk);

        List<ThreadMessage> messages = await thread
            .OrderBy(m => m.ReceivedDate).ThenBy(m => m.Id)
            .Take(MaxThreadMessages)
            .Select(m => new ThreadMessage(
                m.Id, m.FolderId, m.Folder!.Kind, m.Subject, m.FromName, m.FromAddress, m.ToSummary, m.Preview, m.ReceivedDate, m.IsRead, m.IsStarred, m.HasAttachments))
            .ToListAsync(cancel);

        // The message that was asked for is always there, whatever the rules above made of it.
        if (messages.All(m => m.Id != anchor.Id))
        {
            messages.Add(new ThreadMessage(
                anchor.Id, anchor.FolderId, anchorFolder, anchor.Subject, anchor.FromName, anchor.FromAddress, anchor.ToSummary, anchor.Preview, anchor.ReceivedDate,
                anchor.IsRead, anchor.IsStarred, anchor.HasAttachments));
            messages = messages.OrderBy(m => m.ReceivedDate).ThenBy(m => m.Id).ToList();
        }

        return messages;
    }

    /// <summary>The subject of a conversation: the one of its newest message without "Re:", "AW:" and the like.</summary>
    private static string StripReplyPrefixes(string subject)
    {
        string stripped = MessageParser.StripReplyPrefixes(subject);
        return stripped.Length > 0 ? stripped : subject;
    }
}

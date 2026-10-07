using System.Data;
using System.Data.Common;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MatMail.Messaging;

/// <summary>A message to store: the raw bytes plus the state it arrives with.</summary>
public sealed record NewMessage(byte[] Raw)
{
    /// <summary>IMAP INTERNALDATE. Default: now.</summary>
    public DateTime? ReceivedDate { get; init; }
    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public bool IsAnswered { get; init; }
    public bool IsForwarded { get; init; }
    public bool IsDraft { get; init; }
    public bool IsDeleted { get; init; }
    public string[]? Keywords { get; init; }

    public long? SourceAccountId { get; init; }
    public string? RemoteFolder { get; init; }
    public string? RemoteUid { get; init; }

    /// <summary>The addresses this copy was routed for; shown in the "Unassigned" view.</summary>
    public string? EnvelopeRecipients { get; init; }

    /// <summary>Remote: only the metadata is stored, the bytes stay at the provider (<see cref="ServerRetention.LiveAccess"/>).</summary>
    public MessageStorage Storage { get; init; } = MessageStorage.Local;
}

/// <summary>A change of message flags. Properties left null stay as they are.</summary>
public sealed record FlagChange
{
    public bool? IsRead { get; init; }
    public bool? IsStarred { get; init; }
    public bool? IsAnswered { get; init; }
    public bool? IsForwarded { get; init; }
    public bool? IsDraft { get; init; }
    public bool? IsDeleted { get; init; }
    public string[]? AddKeywords { get; init; }
    public string[]? RemoveKeywords { get; init; }

    /// <summary>Replace all keywords.</summary>
    public string[]? SetKeywords { get; init; }
}

/// <summary>
/// The mail store: puts messages into folders and moves, flags and deletes them. Everything that holds mail goes through here
/// (SMTP delivery, provider sync, the web client, IMAP), so UIDs and the change counter of a folder stay consistent and the
/// other sessions get told. Access rights are checked by the callers; the tenant filter of the database context applies.
/// </summary>
public sealed class MailStore
{
    private readonly MatMailDbContext _db;
    private readonly MailEventHub _hub;
    private readonly IServiceProvider _services;

    public MailStore(MatMailDbContext db, MailEventHub hub, IServiceProvider services)
    {
        _db = db;
        _hub = hub;
        _services = services;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Adding
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Stores a message in a folder: assigns the next UID and notifies the listeners.</summary>
    public async Task<MailMessage> AddAsync(long folderId, NewMessage input, CancellationToken cancel = default)
    {
        MailFolder folder = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId, cancel)
            ?? throw new InvalidOperationException($"Folder {folderId} does not exist.");
        ParsedMessage parsed = SafeParse(input.Raw);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancel);
        (long uid, long modSeq) = await AllocateAsync(folderId, 1, cancel);

        var message = new MailMessage
        {
            TenantId = folder.TenantId,
            MailboxId = folder.MailboxId,
            FolderId = folderId,
            Uid = uid,
            ModSeq = modSeq,
            MessageIdHeader = parsed.MessageId,
            InReplyTo = parsed.InReplyTo,
            ReferencesHeader = parsed.References,
            ThreadKey = parsed.ThreadKey,
            Subject = parsed.Subject,
            FromName = parsed.FromName,
            FromAddress = parsed.FromAddress,
            ToSummary = parsed.ToSummary,
            SentDate = parsed.SentDate,
            ReceivedDate = (input.ReceivedDate ?? DateTime.UtcNow).ToUniversalTime(),
            SizeBytes = input.Raw.LongLength,
            Preview = parsed.Preview,
            IsRead = input.IsRead,
            IsStarred = input.IsStarred,
            IsAnswered = input.IsAnswered,
            IsForwarded = input.IsForwarded,
            IsDraft = input.IsDraft,
            IsDeleted = input.IsDeleted,
            HasAttachments = parsed.HasAttachments,
            Keywords = input.Keywords ?? Array.Empty<string>(),
            Storage = input.Storage,
            SourceAccountId = input.SourceAccountId,
            RemoteFolder = input.RemoteFolder,
            RemoteUid = input.RemoteUid,
            EnvelopeRecipients = input.EnvelopeRecipients,
            Content = new MailMessageContent
            {
                Raw = input.Storage == MessageStorage.Local ? input.Raw : null,
                HeaderBytes = parsed.HeaderBytes,
                SearchText = parsed.SearchText,
            },
        };

        _db.MailMessages.Add(message);
        await _db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);

        _hub.Publish(new MailEvent(MailEventKind.NewMessage, folder.TenantId, folder.MailboxId, folderId, message.Id));
        return message;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The raw message. For messages that are only referenced (live access) the provider is asked.</summary>
    public async Task<byte[]?> GetRawAsync(long messageId, CancellationToken cancel = default)
    {
        MailMessageContent? content = await _db.MailMessageContents.AsNoTracking().FirstOrDefaultAsync(c => c.MessageId == messageId, cancel);
        if (content?.Raw is not null)
        {
            return content.Raw;
        }

        MailMessage? message = await _db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, cancel);
        if (message is null || message.Storage != MessageStorage.Remote)
        {
            return null;
        }

        var provider = _services.GetService<IRemoteContentProvider>();
        return provider is null ? null : await provider.FetchAsync(message, cancel);
    }

    /// <summary>Only the header block (cheap; for envelopes and header fetches).</summary>
    public async Task<byte[]?> GetHeaderBytesAsync(long messageId, CancellationToken cancel = default)
        => await _db.MailMessageContents.AsNoTracking().Where(c => c.MessageId == messageId).Select(c => c.HeaderBytes).FirstOrDefaultAsync(cancel);

    /// <summary>Messages and unread messages per folder (deleted-marked messages do not count as unread).</summary>
    public async Task<Dictionary<long, (int Total, int Unread)>> GetCountsAsync(IEnumerable<long> folderIds, CancellationToken cancel = default)
    {
        long[] ids = folderIds.ToArray();
        var rows = await _db.MailMessages.AsNoTracking()
            .Where(m => ids.Contains(m.FolderId))
            .GroupBy(m => m.FolderId)
            .Select(g => new { FolderId = g.Key, Total = g.Count(), Unread = g.Count(m => !m.IsRead && !m.IsDeleted) })
            .ToListAsync(cancel);
        return ids.ToDictionary(id => id, id => rows.Where(r => r.FolderId == id).Select(r => (r.Total, r.Unread)).FirstOrDefault());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Changing
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Applies a flag change to messages. Returns how many were changed.</summary>
    public async Task<int> ChangeFlagsAsync(IEnumerable<long> messageIds, FlagChange change, CancellationToken cancel = default)
    {
        long[] ids = messageIds.Distinct().ToArray();
        List<MailMessage> messages = await _db.MailMessages.Where(m => ids.Contains(m.Id)).ToListAsync(cancel);
        int changed = 0;

        foreach (IGrouping<long, MailMessage> folderGroup in messages.GroupBy(m => m.FolderId))
        {
            var touched = new List<MailMessage>();
            foreach (MailMessage message in folderGroup)
            {
                if (Apply(message, change))
                {
                    touched.Add(message);
                }
            }

            if (touched.Count == 0)
            {
                continue;
            }

            long modSeq = await BumpAsync(folderGroup.Key, cancel);
            touched.ForEach(m => m.ModSeq = modSeq);
            changed += touched.Count;
        }

        if (changed > 0)
        {
            await _db.SaveChangesAsync(cancel);
            foreach (IGrouping<long, MailMessage> folderGroup in messages.GroupBy(m => m.FolderId))
            {
                MailMessage first = folderGroup.First();
                _hub.Publish(new MailEvent(MailEventKind.FlagsChanged, first.TenantId, first.MailboxId, folderGroup.Key));
            }
        }

        return changed;
    }

    /// <summary>Marks every unread message of a folder as read.</summary>
    public async Task<int> MarkFolderReadAsync(long folderId, CancellationToken cancel = default)
    {
        long[] ids = await _db.MailMessages.Where(m => m.FolderId == folderId && !m.IsRead).Select(m => m.Id).ToArrayAsync(cancel);
        return ids.Length == 0 ? 0 : await ChangeFlagsAsync(ids, new FlagChange { IsRead = true }, cancel);
    }

    /// <summary>
    /// Moves messages into a folder (possibly of another mailbox of the same tenant). They get new UIDs there; the old folder
    /// reports them as removed.
    /// </summary>
    public async Task<IReadOnlyList<MailMessage>> MoveAsync(IEnumerable<long> messageIds, long targetFolderId, CancellationToken cancel = default)
    {
        long[] ids = messageIds.Distinct().ToArray();
        MailFolder target = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == targetFolderId, cancel)
            ?? throw new InvalidOperationException($"Folder {targetFolderId} does not exist.");
        List<MailMessage> messages = await _db.MailMessages.Where(m => ids.Contains(m.Id) && m.FolderId != targetFolderId).OrderBy(m => m.FolderId).ThenBy(m => m.Uid).ToListAsync(cancel);
        if (messages.Count == 0)
        {
            return messages;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancel);
        (long firstUid, long targetModSeq) = await AllocateAsync(targetFolderId, messages.Count, cancel);

        var sources = messages.GroupBy(m => (m.FolderId, m.MailboxId)).Select(g => g.Key).ToList();
        foreach (long sourceFolder in sources.Select(s => s.FolderId).Distinct())
        {
            await BumpAsync(sourceFolder, cancel);
        }

        long uid = firstUid;
        foreach (MailMessage message in messages)
        {
            message.FolderId = targetFolderId;
            message.MailboxId = target.MailboxId;
            message.Uid = uid++;
            message.ModSeq = targetModSeq;
        }

        await _db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);

        foreach ((long folderId, long mailboxId) in sources)
        {
            _hub.Publish(new MailEvent(MailEventKind.Removed, target.TenantId, mailboxId, folderId));
        }

        _hub.Publish(new MailEvent(MailEventKind.NewMessage, target.TenantId, target.MailboxId, targetFolderId));
        return messages;
    }

    /// <summary>Copies messages into a folder (new messages with the same content, flags and dates).</summary>
    public async Task<IReadOnlyList<MailMessage>> CopyAsync(IEnumerable<long> messageIds, long targetFolderId, CancellationToken cancel = default)
    {
        long[] ids = messageIds.Distinct().ToArray();
        List<MailMessage> sources = await _db.MailMessages.AsNoTracking().Include(m => m.Content)
            .Where(m => ids.Contains(m.Id)).OrderBy(m => m.FolderId).ThenBy(m => m.Uid).ToListAsync(cancel);

        var copies = new List<MailMessage>();
        foreach (MailMessage source in sources)
        {
            byte[]? raw = source.Content?.Raw;
            if (raw is null && source.Storage == MessageStorage.Remote)
            {
                raw = await GetRawAsync(source.Id, cancel);
            }

            if (raw is null)
            {
                continue;
            }

            copies.Add(await AddAsync(targetFolderId, new NewMessage(raw)
            {
                ReceivedDate = source.ReceivedDate,
                IsRead = source.IsRead,
                IsStarred = source.IsStarred,
                IsAnswered = source.IsAnswered,
                IsForwarded = source.IsForwarded,
                IsDraft = source.IsDraft,
                Keywords = source.Keywords,
                EnvelopeRecipients = source.EnvelopeRecipients,
            }, cancel));
        }

        return copies;
    }

    /// <summary>
    /// Deletes messages: into the trash folder of their mailbox, or for good when they already are in the trash (or when
    /// <paramref name="permanent"/> is set). Returns the number of affected messages.
    /// </summary>
    public async Task<int> DeleteAsync(IEnumerable<long> messageIds, bool permanent = false, CancellationToken cancel = default)
    {
        long[] ids = messageIds.Distinct().ToArray();
        List<MailMessage> messages = await _db.MailMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(cancel);
        if (messages.Count == 0)
        {
            return 0;
        }

        Dictionary<long, FolderKind> kinds = await _db.MailFolders.AsNoTracking()
            .Where(f => messages.Select(m => m.FolderId).Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Kind, cancel);

        int affected = 0;
        foreach (IGrouping<long, MailMessage> mailbox in messages.GroupBy(m => m.MailboxId))
        {
            MailFolder? trash = await _db.MailFolders.AsNoTracking().FirstOrDefaultAsync(f => f.MailboxId == mailbox.Key && f.Kind == FolderKind.Trash, cancel);
            long[] toTrash = permanent || trash is null ? Array.Empty<long>() : mailbox.Where(m => kinds[m.FolderId] != FolderKind.Trash).Select(m => m.Id).ToArray();
            long[] forever = mailbox.Select(m => m.Id).Except(toTrash).ToArray();

            if (toTrash.Length > 0)
            {
                await MoveAsync(toTrash, trash!.Id, cancel);
                affected += toTrash.Length;
            }

            if (forever.Length > 0)
            {
                affected += await RemoveAsync(forever, cancel);
            }
        }

        return affected;
    }

    /// <summary>IMAP EXPUNGE: removes the messages of a folder that are marked deleted (optionally only the given UIDs).</summary>
    public async Task<IReadOnlyList<long>> ExpungeAsync(long folderId, IReadOnlyCollection<long>? onlyUids = null, CancellationToken cancel = default)
    {
        IQueryable<MailMessage> query = _db.MailMessages.Where(m => m.FolderId == folderId && m.IsDeleted);
        if (onlyUids is not null)
        {
            query = query.Where(m => onlyUids.Contains(m.Uid));
        }

        List<long> ids = await query.Select(m => m.Id).ToListAsync(cancel);
        List<long> uids = await query.Select(m => m.Uid).OrderBy(u => u).ToListAsync(cancel);
        if (ids.Count > 0)
        {
            await RemoveAsync(ids, cancel);
        }

        return uids;
    }

    /// <summary>Removes every message of a folder for good (empty trash).</summary>
    public async Task<int> EmptyFolderAsync(long folderId, CancellationToken cancel = default)
    {
        long[] ids = await _db.MailMessages.Where(m => m.FolderId == folderId).Select(m => m.Id).ToArrayAsync(cancel);
        return ids.Length == 0 ? 0 : await RemoveAsync(ids, cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<int> RemoveAsync(IReadOnlyCollection<long> ids, CancellationToken cancel)
    {
        var locations = await _db.MailMessages.AsNoTracking().Where(m => ids.Contains(m.Id))
            .Select(m => new { m.TenantId, m.MailboxId, m.FolderId }).Distinct().ToListAsync(cancel);

        int deleted = await _db.MailMessages.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(cancel);
        foreach (var location in locations)
        {
            await BumpAsync(location.FolderId, cancel);
            _hub.Publish(new MailEvent(MailEventKind.Removed, location.TenantId, location.MailboxId, location.FolderId));
        }

        return deleted;
    }

    private static bool Apply(MailMessage message, FlagChange change)
    {
        bool changed = false;
        changed |= Set(change.IsRead, message.IsRead, v => message.IsRead = v);
        changed |= Set(change.IsStarred, message.IsStarred, v => message.IsStarred = v);
        changed |= Set(change.IsAnswered, message.IsAnswered, v => message.IsAnswered = v);
        changed |= Set(change.IsForwarded, message.IsForwarded, v => message.IsForwarded = v);
        changed |= Set(change.IsDraft, message.IsDraft, v => message.IsDraft = v);
        changed |= Set(change.IsDeleted, message.IsDeleted, v => message.IsDeleted = v);

        if (change.SetKeywords is not null || change.AddKeywords is not null || change.RemoveKeywords is not null)
        {
            var keywords = new HashSet<string>(change.SetKeywords ?? message.Keywords, StringComparer.OrdinalIgnoreCase);
            foreach (string add in change.AddKeywords ?? Array.Empty<string>())
            {
                keywords.Add(add);
            }

            foreach (string remove in change.RemoveKeywords ?? Array.Empty<string>())
            {
                keywords.Remove(remove);
            }

            string[] updated = keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!updated.SequenceEqual(message.Keywords, StringComparer.OrdinalIgnoreCase))
            {
                message.Keywords = updated;
                changed = true;
            }
        }

        return changed;

        static bool Set(bool? wanted, bool current, Action<bool> assign)
        {
            if (wanted is null || wanted == current)
            {
                return false;
            }

            assign(wanted.Value);
            return true;
        }
    }

    private static ParsedMessage SafeParse(byte[] raw)
    {
        try
        {
            return MessageParser.Parse(raw);
        }
        catch (Exception)
        {
            // A message nobody can parse is still stored (and shown) rather than lost.
            return new ParsedMessage { Subject = "(unreadable message)", ThreadKey = "subj:(unreadable message)", HeaderBytes = raw.Length > 4096 ? raw[..4096] : raw };
        }
    }

    /// <summary>Reserves <paramref name="count"/> UIDs of a folder and bumps its change counter in one statement (race free).</summary>
    private async Task<(long FirstUid, long ModSeq)> AllocateAsync(long folderId, int count, CancellationToken cancel)
    {
        DbConnection connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancel);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "UPDATE \"MailFolder\" SET \"UidNext\" = \"UidNext\" + @count, \"ModSeq\" = \"ModSeq\" + 1 WHERE \"Id\" = @id " +
                              "RETURNING \"UidNext\" - @count, \"ModSeq\"";
        AddParameter(command, "count", (long)count);
        AddParameter(command, "id", folderId);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancel);
        if (!await reader.ReadAsync(cancel))
        {
            throw new InvalidOperationException($"Folder {folderId} does not exist.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Counts one change of the folder (so IMAP sessions and the web client notice) and returns the new counter.</summary>
    private async Task<long> BumpAsync(long folderId, CancellationToken cancel)
    {
        DbConnection connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancel);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "UPDATE \"MailFolder\" SET \"ModSeq\" = \"ModSeq\" + 1 WHERE \"Id\" = @id RETURNING \"ModSeq\"";
        AddParameter(command, "id", folderId);
        object? result = await command.ExecuteScalarAsync(cancel);
        return result is long value ? value : 0;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Fetches the bytes of messages that are only referenced locally (provider accounts with live access). Registered by the sync module.</summary>
public interface IRemoteContentProvider
{
    Task<byte[]?> FetchAsync(MailMessage message, CancellationToken cancel);
}

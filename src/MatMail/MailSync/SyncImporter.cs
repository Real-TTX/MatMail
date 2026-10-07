using System.Collections.Concurrent;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;
using Npgsql;
using MailFolder = MatMail.Data.MailFolder;
using MailStore = MatMail.Messaging.MailStore;

namespace MatMail.MailSync;

/// <summary>A remote message about to be imported.</summary>
public sealed record RemoteMessage
{
    public required RemoteFolderInfo Folder { get; init; }

    /// <summary>IMAP UID (decimal) or POP3 UIDL.</summary>
    public required string Uid { get; init; }

    public long? Size { get; init; }

    /// <summary>IMAP INTERNALDATE; for POP3 it is taken from the Received headers after the download.</summary>
    public DateTime? InternalDate { get; init; }

    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public bool IsAnswered { get; init; }
    public bool IsDraft { get; init; }
    public string[]? Keywords { get; init; }

    /// <summary>"&lt;id@host&gt;" from the IMAP envelope, when known before the download.</summary>
    public string? MessageId { get; init; }

    /// <summary>Live access: the header-only stand-in that is stored instead of the message.</summary>
    public byte[]? Stub { get; init; }

    public bool HasAttachments { get; init; }
}

public enum ImportStatus
{
    /// <summary>A local copy was created.</summary>
    Stored,
    /// <summary>A local copy existed already (same message in the mailbox, or found again after the provider renumbered the folder).</summary>
    AlreadyPresent,
    /// <summary>Recorded in an earlier run.</summary>
    Known,
    /// <summary>Gone at the provider before it could be fetched.</summary>
    Vanished,
    /// <summary>Larger than the limit: recorded without a local copy and left at the provider.</summary>
    TooLarge,
    /// <summary>Failed this time; retried in the next run.</summary>
    Failed,
    /// <summary>Failed too often: recorded without a local copy and left at the provider.</summary>
    GivenUp,
}

public readonly record struct ImportResult(ImportStatus Status, long? LocalMessageId = null)
{
    /// <summary>Dealt with: the folder position may move past it.</summary>
    public bool IsDone => Status != ImportStatus.Failed;

    /// <summary>A local copy exists, so "delete after download" may remove the original.</summary>
    public bool HasLocalCopy => LocalMessageId is not null && Status is ImportStatus.Stored or ImportStatus.AlreadyPresent or ImportStatus.Known;
}

/// <summary>Where the synchronisation of a remote folder stands (the persisted <see cref="MailAccountFolderState"/>).</summary>
public sealed class FolderPosition
{
    public long StateId { get; init; }
    public required string RemoteFolder { get; init; }
    public uint UidValidity { get; init; }
    public long LastUid { get; set; }
}

/// <summary>A folder cannot be synchronised (e.g. its local counterpart cannot be created); the other folders go on.</summary>
public sealed class SyncFolderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Counts failed import attempts per remote message in memory; after <see cref="MailSyncOptions.MaxMessageAttempts"/> the message is
/// given up. A restart gives a broken message a few more attempts, which is harmless.
/// </summary>
public sealed class SyncFailureTracker
{
    private readonly ConcurrentDictionary<(long AccountId, string Folder, string Uid), int> _attempts = new();

    public int RecordFailure(long accountId, string folder, string uid)
        => _attempts.AddOrUpdate((accountId, folder, uid), 1, (_, count) => count + 1);

    public void Forget(long accountId, string folder, string uid) => _attempts.TryRemove((accountId, folder, uid), out _);

    public void ForgetFolder(long accountId, string folder)
    {
        foreach (var key in _attempts.Keys.Where(k => k.AccountId == accountId && k.Folder == folder))
        {
            _attempts.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Stores fetched provider messages by the account's role and records them, so nothing is fetched twice. Everyday mail is routed by
/// recipient (unclaimed mail may go to the account's fallback mailbox), backups land below "Backup/&lt;account&gt;/", migrations
/// mirror the remote folder tree in the target mailbox, live access stores header-only stand-ins. A broken message never blocks
/// the account. One instance serves one run (scoped); the change tracker is cleared after every message, so a long run does not
/// keep message bytes in memory.
/// </summary>
public sealed class SyncImporter
{
    private readonly MatMailDbContext _db;
    private readonly MailDelivery _delivery;
    private readonly MailStore _store;
    private readonly FolderService _folders;
    private readonly MailboxService _mailboxes;
    private readonly SyncFailureTracker _failures;
    private readonly ActivityLogger _activity;
    private readonly MailSyncOptions _options;
    private readonly ILogger<SyncImporter> _logger;
    private readonly Dictionary<string, long> _localFolders = new(StringComparer.Ordinal);
    private long? _fallbackInboxId;

    public SyncImporter(
        MatMailDbContext db, MailDelivery delivery, MailStore store, FolderService folders, MailboxService mailboxes,
        SyncFailureTracker failures, ActivityLogger activity, MailSyncOptions options, ILogger<SyncImporter> logger)
    {
        _db = db;
        _delivery = delivery;
        _store = store;
        _folders = folders;
        _mailboxes = mailboxes;
        _failures = failures;
        _activity = activity;
        _options = options;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Preparing a run
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks the target mailbox: required for backups and migrations; for everyday mail the optional fallback for unclaimed mail.
    /// Only mailboxes of the account's own tenant count.
    /// </summary>
    public async Task PrepareAsync(SyncRun run, CancellationToken cancel)
    {
        MailAccount account = run.Account;
        Mailbox? target = account.TargetMailboxId is long targetId
            ? await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == targetId && m.TenantId == account.TenantId, cancel)
            : null;

        if (run.Role is MailAccountRole.Backup or MailAccountRole.Migration)
        {
            string purpose = run.Role == MailAccountRole.Backup ? "backup" : "migration";
            if (target is null)
            {
                throw new SyncConfigurationException($"The {purpose} needs a target mailbox; choose one in the account settings.");
            }

            if (!target.IsActive)
            {
                throw new SyncConfigurationException($"The target mailbox \"{target.Name}\" of the {purpose} is deactivated.");
            }

            await _mailboxes.EnsureDefaultFoldersAsync(target);
            run.TargetMailbox = target;
        }
        else if (account.TargetMailboxId is not null)
        {
            if (target is { IsActive: true })
            {
                await _mailboxes.EnsureDefaultFoldersAsync(target);
                run.TargetMailbox = target;
                _fallbackInboxId = (await _folders.FindByKindAsync(target.Id, FolderKind.Inbox, cancel))?.Id;
            }
            else
            {
                run.Note("the fallback mailbox for unclaimed mail is missing or deactivated, such mail stays in Unassigned");
            }
        }

        _db.ChangeTracker.Clear();
    }

    /// <summary>Backup and migration: finds or creates the local folder of a remote folder before its messages are imported.</summary>
    public async Task PrepareFolderAsync(SyncRun run, RemoteFolderInfo remote, CancellationToken cancel)
    {
        if (run.Role == MailAccountRole.Mail || _localFolders.ContainsKey(remote.FullName))
        {
            return;
        }

        Mailbox target = run.TargetMailbox ?? throw new SyncConfigurationException("The account has no target mailbox.");
        try
        {
            MailFolder folder;
            if (run.Role == MailAccountRole.Backup)
            {
                folder = await _folders.EnsureAsync(target.Id, RemoteFolderMap.BackupPathOf(run.Account.Name, remote), cancel);
            }
            else
            {
                FolderKind kind = RemoteFolderMap.KindOf(remote);
                folder = kind == FolderKind.Custom
                    ? await _folders.EnsureAsync(target.Id, RemoteFolderMap.LocalPathOf(remote), cancel)
                    : await _folders.FindByKindAsync(target.Id, kind, cancel) ?? throw new InvalidOperationException($"The target mailbox has no {kind} folder.");
            }

            _localFolders[remote.FullName] = folder.Id;
        }
        catch (InvalidOperationException ex)
        {
            throw new SyncFolderException($"no local folder for it: {ex.Message}", ex);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Folder positions (IMAP)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The position of a remote folder; created on the first visit. When the provider renumbered the folder (UIDVALIDITY changed)
    /// everything recorded for it is forgotten and the folder is compared again from the start.
    /// </summary>
    public async Task<FolderPosition> OpenPositionAsync(SyncRun run, string remoteFolder, uint uidValidity, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        MailAccountFolderState? state = await _db.MailAccountFolderStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.MailAccountId == accountId && s.RemoteFolder == remoteFolder, cancel);
        if (state is null)
        {
            state = new MailAccountFolderState { MailAccountId = accountId, RemoteFolder = remoteFolder, UidValidity = uidValidity };
            _db.MailAccountFolderStates.Add(state);
            await _db.SaveChangesAsync(cancel);
            _db.ChangeTracker.Clear();
            return new FolderPosition { StateId = state.Id, RemoteFolder = remoteFolder, UidValidity = uidValidity };
        }

        long lastUid = state.LastUid;
        if (state.UidValidity != uidValidity)
        {
            if (state.UidValidity != 0 && uidValidity != 0)
            {
                await ResetFolderAsync(run, remoteFolder, cancel);
                lastUid = 0;
            }

            long stateId = state.Id;
            DateTime now = DateTime.UtcNow;
            await _db.MailAccountFolderStates.Where(s => s.Id == stateId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.UidValidity, (long)uidValidity)
                .SetProperty(x => x.LastUid, lastUid)
                .SetProperty(x => x.UpdateDate, now), cancel);
        }

        return new FolderPosition { StateId = state.Id, RemoteFolder = remoteFolder, UidValidity = uidValidity, LastUid = lastUid };
    }

    /// <summary>Stores the position after a committed batch.</summary>
    public async Task SavePositionAsync(FolderPosition position, CancellationToken cancel)
    {
        long stateId = position.StateId;
        long lastUid = position.LastUid;
        DateTime now = DateTime.UtcNow;
        await _db.MailAccountFolderStates.Where(s => s.Id == stateId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastUid, lastUid)
            .SetProperty(x => x.UpdateDate, now), cancel);
    }

    /// <summary>Notes that the folder was synchronised completely.</summary>
    public async Task FinishFolderAsync(FolderPosition position, CancellationToken cancel)
    {
        long stateId = position.StateId;
        long lastUid = position.LastUid;
        DateTime now = DateTime.UtcNow;
        await _db.MailAccountFolderStates.Where(s => s.Id == stateId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastUid, lastUid)
            .SetProperty(x => x.LastSyncDate, now)
            .SetProperty(x => x.UpdateDate, now), cancel);
    }

    /// <summary>Shows that the run is alive (a run silent for <see cref="MailSyncOptions.StaleRunAfter"/> counts as crashed).</summary>
    public async Task HeartbeatAsync(SyncRun run, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        DateTime now = DateTime.UtcNow;
        await _db.MailAccounts.Where(a => a.Id == accountId && a.LastSyncState == SyncState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdateDate, now), cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Known remote messages
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The recorded messages of a remote folder (UID → local message id; null = recorded without a local copy).</summary>
    public async Task<Dictionary<string, long?>> KnownAsync(SyncRun run, string remoteFolder, IReadOnlyCollection<string>? onlyUids, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        IQueryable<RemoteMessageState> query = _db.RemoteMessageStates.AsNoTracking()
            .Where(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder);
        if (onlyUids is not null)
        {
            string[] uids = onlyUids.ToArray();
            query = query.Where(r => uids.Contains(r.RemoteUid));
        }

        return await query.ToDictionaryAsync(r => r.RemoteUid, r => r.LocalMessageId, StringComparer.Ordinal, cancel);
    }

    public Task<int> CountKnownAsync(SyncRun run, string remoteFolder, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        return _db.RemoteMessageStates.CountAsync(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder, cancel);
    }

    /// <summary>Drops the records of messages that are gone at the provider.</summary>
    public async Task ForgetAsync(SyncRun run, string remoteFolder, IReadOnlyCollection<string> uids, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        foreach (string[] chunk in uids.Chunk(1000))
        {
            await _db.RemoteMessageStates
                .Where(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder && chunk.Contains(r.RemoteUid))
                .ExecuteDeleteAsync(cancel);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Importing
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Imports one remote message: downloads it (<paramref name="download"/>; not used for live access, where
    /// <see cref="RemoteMessage.Stub"/> is stored), stores it by role and records it. Problems of this message are counted and,
    /// after a few attempts, the message is given up; connection and database server problems are passed on and end the run.
    /// </summary>
    public async Task<ImportResult> ImportAsync(SyncRun run, RemoteMessage remote, Func<CancellationToken, Task<byte[]>> download, CancellationToken cancel)
    {
        try
        {
            _options.BeforeImport?.Invoke(run.Account.Id, remote.Folder.FullName, remote.Uid);
            ImportResult result = await ImportOnceAsync(run, remote, download, cancel);
            _failures.Forget(run.Account.Id, remote.Folder.FullName, remote.Uid);
            return result;
        }
        catch (Exception ex) when (IsMessageProblem(ex))
        {
            _db.ChangeTracker.Clear();
            return await HandleFailureAsync(run, remote, ex, cancel);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// True for problems of this one message (unreadable, refused by the database, NO from the server). Problems of the connection
    /// or of the database server are not the message's fault: they end the run and the message is tried again later.
    /// </summary>
    public static bool IsMessageProblem(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or IOException or SocketException or TimeoutException
                or ServiceNotConnectedException or ServiceNotAuthenticatedException or ImapProtocolException or Pop3ProtocolException
                or SyncConfigurationException or SyncFolderException or NpgsqlException { IsTransient: true })
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ImportResult> ImportOnceAsync(SyncRun run, RemoteMessage remote, Func<CancellationToken, Task<byte[]>> download, CancellationToken cancel)
    {
        ImportResult result;
        if (run.IsLiveAccess)
        {
            // Nothing is downloaded, so there is no size limit: the body is fetched when somebody opens the message.
            result = await DeliverAsync(run, remote, remote.Stub ?? "\r\n"u8.ToArray(), MessageStorage.Remote, cancel);
        }
        else if (remote.Size > _options.MaxMessageBytes)
        {
            return await SkipTooLargeAsync(run, remote, cancel);
        }
        else if (await RelinkAsync(run, remote, cancel) is ImportResult relinked)
        {
            result = relinked;
        }
        else
        {
            byte[] raw = await download(cancel);
            if (raw.LongLength > _options.MaxMessageBytes)
            {
                return await SkipTooLargeAsync(run, remote, cancel);
            }

            RemoteMessage dated = remote.InternalDate is null ? remote with { InternalDate = MessageDates.ReceivedDateOf(raw) } : remote;
            result = run.Role == MailAccountRole.Mail
                ? await DeliverAsync(run, dated, raw, MessageStorage.Local, cancel)
                : await StoreCopyAsync(run, dated, raw, cancel);
        }

        await RecordAsync(run, remote, result.LocalMessageId, cancel);
        if (result.Status == ImportStatus.Stored)
        {
            run.Downloaded++;
        }
        else
        {
            run.AlreadyPresent++;
        }

        return result;
    }

    /// <summary>Everyday mail and live access: routing by recipient (the core falls back to Unassigned).</summary>
    private async Task<ImportResult> DeliverAsync(SyncRun run, RemoteMessage remote, byte[] raw, MessageStorage storage, CancellationToken cancel)
    {
        DeliveryResult delivery = await _delivery.DeliverAsync(raw, new DeliverySource
        {
            Account = run.Account,
            TenantId = run.Account.TenantId,
            RemoteFolder = remote.Folder.FullName,
            RemoteUid = remote.Uid,
            ReceivedDate = remote.InternalDate,
            IsRead = remote.IsRead,
            IsStarred = remote.IsStarred,
            IsAnswered = remote.IsAnswered,
            Keywords = remote.Keywords,
            Storage = storage,
        }, cancel);

        if (delivery.Copies.Count == 0)
        {
            throw new InvalidOperationException("The message could not be delivered to any mailbox.");
        }

        if (storage == MessageStorage.Remote)
        {
            await DescribeLiveCopiesAsync(remote, delivery, cancel);
        }

        await MoveUnclaimedAsync(delivery, cancel);
        DeliveredCopy first = delivery.Copies[0];
        ImportStatus status = delivery.Copies.All(c => c.WasDuplicate) ? ImportStatus.AlreadyPresent : ImportStatus.Stored;
        return new ImportResult(status, first.Message?.Id);
    }

    /// <summary>The stand-ins of live access get the real size and attachment flag (the stored stub is only the header).</summary>
    private async Task DescribeLiveCopiesAsync(RemoteMessage remote, DeliveryResult delivery, CancellationToken cancel)
    {
        long[] created = delivery.Copies.Where(c => !c.WasDuplicate && c.Message is not null).Select(c => c.Message!.Id).ToArray();
        if (created.Length == 0 || remote.Size is not long size)
        {
            return;
        }

        bool hasAttachments = remote.HasAttachments;
        await _db.MailMessages.Where(m => created.Contains(m.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.SizeBytes, size)
            .SetProperty(m => m.HasAttachments, hasAttachments), cancel);
    }

    /// <summary>Mail that only the "Unassigned" mailbox took goes to the account's fallback mailbox instead, when one is set.</summary>
    private async Task MoveUnclaimedAsync(DeliveryResult delivery, CancellationToken cancel)
    {
        if (_fallbackInboxId is not long inboxId || delivery.Copies.Count != 1)
        {
            return;
        }

        DeliveredCopy copy = delivery.Copies[0];
        if (copy is { WentToUnassigned: true, WasDuplicate: false, Message: not null })
        {
            await _store.MoveAsync(new[] { copy.Message.Id }, inboxId, cancel);
        }
    }

    /// <summary>Backup and migration: the copy goes into the prepared local folder as it is (flags and date kept, no routing).</summary>
    private async Task<ImportResult> StoreCopyAsync(SyncRun run, RemoteMessage remote, byte[] raw, CancellationToken cancel)
    {
        await PrepareFolderAsync(run, remote.Folder, cancel);
        MailMessage stored = await _store.AddAsync(_localFolders[remote.Folder.FullName], new NewMessage(raw)
        {
            ReceivedDate = remote.InternalDate,
            IsRead = remote.IsRead,
            IsStarred = remote.IsStarred,
            IsAnswered = remote.IsAnswered,
            IsDraft = remote.IsDraft,
            Keywords = remote.Keywords,
            SourceAccountId = run.Account.Id,
            RemoteFolder = remote.Folder.FullName,
            RemoteUid = remote.Uid,
        }, cancel);
        return new ImportResult(ImportStatus.Stored, stored.Id);
    }

    /// <summary>
    /// Backup and migration: a local copy of this account and folder with the same Message-ID that no record points to any more
    /// (the provider renumbered the folder, or a run was interrupted between storing and recording) is taken again instead of
    /// storing the message a second time.
    /// </summary>
    private async Task<ImportResult?> RelinkAsync(SyncRun run, RemoteMessage remote, CancellationToken cancel)
    {
        if (run.Role == MailAccountRole.Mail || remote.MessageId is null)
        {
            return null;
        }

        long accountId = run.Account.Id;
        string folder = remote.Folder.FullName;
        string messageId = remote.MessageId;
        long? orphan = await _db.MailMessages.AsNoTracking()
            .Where(m => m.SourceAccountId == accountId && m.RemoteFolder == folder && m.MessageIdHeader == messageId)
            .Where(m => !_db.RemoteMessageStates.Any(r =>
                r.MailAccountId == accountId && r.RemoteFolder == folder && r.RemoteUid == m.RemoteUid && r.LocalMessageId == m.Id))
            .OrderBy(m => m.Id)
            .Select(m => (long?)m.Id)
            .FirstOrDefaultAsync(cancel);
        if (orphan is not long localId)
        {
            return null;
        }

        string uid = remote.Uid;
        await _db.MailMessages.Where(m => m.Id == localId).ExecuteUpdateAsync(s => s.SetProperty(m => m.RemoteUid, uid), cancel);
        return new ImportResult(ImportStatus.AlreadyPresent, localId);
    }

    /// <summary>Remembers the remote message, so it is never fetched again.</summary>
    private async Task RecordAsync(SyncRun run, RemoteMessage remote, long? localMessageId, CancellationToken cancel)
    {
        _db.ChangeTracker.Clear();
        _db.RemoteMessageStates.Add(new RemoteMessageState
        {
            MailAccountId = run.Account.Id,
            RemoteFolder = remote.Folder.FullName,
            RemoteUid = remote.Uid,
            LocalMessageId = localMessageId,
        });

        try
        {
            await _db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Recorded already by an interrupted earlier run: that record stands.
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private async Task<ImportResult> SkipTooLargeAsync(SyncRun run, RemoteMessage remote, CancellationToken cancel)
    {
        await RecordAsync(run, remote, null, cancel);
        run.TooLarge++;
        await _activity.WarnAsync(
            ActivityCategory.Sync,
            $"{run.Account.Name}: a message in {remote.Folder.FullName} (UID {remote.Uid}, {Megabytes(remote.Size)}) is larger than {Megabytes(_options.MaxMessageBytes)} and is not downloaded; it stays at the provider.",
            tenantId: run.Account.TenantId);
        return new ImportResult(ImportStatus.TooLarge);
    }

    private async Task<ImportResult> HandleFailureAsync(SyncRun run, RemoteMessage remote, Exception ex, CancellationToken cancel)
    {
        string folder = remote.Folder.FullName;
        int attempts = _failures.RecordFailure(run.Account.Id, folder, remote.Uid);
        if (attempts < _options.MaxMessageAttempts)
        {
            run.Failed++;
            _logger.LogWarning(ex, "Message {Uid} in {Folder} of account {AccountId} could not be imported (attempt {Attempt}).", remote.Uid, folder, run.Account.Id, attempts);
            return new ImportResult(ImportStatus.Failed);
        }

        await RecordAsync(run, remote, null, cancel);
        _failures.Forget(run.Account.Id, folder, remote.Uid);
        run.GivenUp++;
        await _activity.WarnAsync(
            ActivityCategory.Sync,
            $"{run.Account.Name}: a message in {folder} (UID {remote.Uid}) could not be imported after {attempts} attempts and is skipped; it stays at the provider.",
            $"{ex.GetType().Name}: {ex.GetBaseException().Message}",
            run.Account.TenantId);
        return new ImportResult(ImportStatus.GivenUp);
    }

    private async Task ResetFolderAsync(SyncRun run, string remoteFolder, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        await _db.RemoteMessageStates.Where(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder).ExecuteDeleteAsync(cancel);

        if (run.IsLiveAccess)
        {
            // The stand-ins point to UIDs that mean other messages now; the folder is listed afresh.
            long[] stubs = await _db.MailMessages
                .Where(m => m.SourceAccountId == accountId && m.RemoteFolder == remoteFolder && m.Storage == MessageStorage.Remote)
                .Select(m => m.Id)
                .ToArrayAsync(cancel);
            if (stubs.Length > 0)
            {
                await _store.DeleteAsync(stubs, permanent: true, cancel);
            }
        }

        _failures.ForgetFolder(accountId, remoteFolder);
        run.Note($"the provider renumbered folder {remoteFolder} (UIDVALIDITY changed), so it was compared again");
        _logger.LogInformation("Account {AccountId}: UIDVALIDITY of {Folder} changed; the folder is compared again.", accountId, remoteFolder);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Flags (IMAP)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Compares read/starred of the local copies with the provider's flags (see <see cref="FlagSync"/>): remote changes are applied
    /// to the local copies here; the local changes are returned to be pushed, then confirmed with <see cref="MarkReconciledAsync"/>.
    /// </summary>
    public async Task<(IReadOnlyList<FlagPush> Push, DateTime ReadAt)> ReconcileFlagsAsync(
        SyncRun run, string remoteFolder, IReadOnlyDictionary<string, RemoteFlags> remote, CancellationToken cancel)
    {
        DateTime readAt = DateTime.UtcNow;
        if (remote.Count == 0)
        {
            return (Array.Empty<FlagPush>(), readAt);
        }

        long accountId = run.Account.Id;
        string[] uids = remote.Keys.ToArray();
        var copies = await _db.MailMessages.AsNoTracking()
            .Where(m => m.SourceAccountId == accountId && m.RemoteFolder == remoteFolder && m.RemoteUid != null && uids.Contains(m.RemoteUid))
            .Select(m => new { m.Id, m.RemoteUid, m.IsRead, m.IsStarred, m.UpdateDate })
            .ToListAsync(cancel);
        Dictionary<string, DateTime> syncedDates = await _db.RemoteMessageStates.AsNoTracking()
            .Where(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder && uids.Contains(r.RemoteUid))
            .ToDictionaryAsync(r => r.RemoteUid, r => r.UpdateDate, StringComparer.Ordinal, cancel);

        IEnumerable<LocalFlags> local = copies
            .Where(c => syncedDates.ContainsKey(c.RemoteUid!))
            .Select(c => new LocalFlags(c.Id, c.RemoteUid!, c.IsRead, c.IsStarred, c.UpdateDate, syncedDates[c.RemoteUid!]));
        FlagPlan plan = FlagSync.Plan(local, remote);

        foreach (var target in plan.Pull.GroupBy(p => (p.IsRead, p.IsStarred)))
        {
            await _store.ChangeFlagsAsync(target.Select(p => p.MessageId), new FlagChange { IsRead = target.Key.IsRead, IsStarred = target.Key.IsStarred }, cancel);
            _db.ChangeTracker.Clear();
        }

        // Pulled copies count as agreeing from now on (after their own change); copies already equal from the time they were read.
        await MarkReconciledAsync(run, remoteFolder, plan.Pull.Select(p => p.RemoteUid).ToArray(), DateTime.UtcNow, cancel);
        await MarkReconciledAsync(run, remoteFolder, plan.InSync, readAt, cancel);
        run.FlagChanges += plan.Pull.Count;
        return (plan.Push, readAt);
    }

    /// <summary>Notes that the local copies of these remote messages agree with the provider as of <paramref name="at"/>.</summary>
    public async Task MarkReconciledAsync(SyncRun run, string remoteFolder, IReadOnlyCollection<string> uids, DateTime at, CancellationToken cancel)
    {
        if (uids.Count == 0)
        {
            return;
        }

        long accountId = run.Account.Id;
        string[] list = uids.ToArray();
        await _db.RemoteMessageStates
            .Where(r => r.MailAccountId == accountId && r.RemoteFolder == remoteFolder && list.Contains(r.RemoteUid))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdateDate, at), cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Live access
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Live access: removes the stand-ins of messages that are gone at the provider (deleted or moved elsewhere by another client),
    /// so the local list follows the provider. Local messages with stored bodies are never touched.
    /// </summary>
    public async Task<int> RemoveVanishedAsync(SyncRun run, FolderPosition position, IReadOnlySet<uint> existing, CancellationToken cancel)
    {
        long accountId = run.Account.Id;
        string folder = position.RemoteFolder;
        List<string> known = await _db.RemoteMessageStates.AsNoTracking()
            .Where(r => r.MailAccountId == accountId && r.RemoteFolder == folder)
            .Select(r => r.RemoteUid)
            .ToListAsync(cancel);
        string[] vanished = known
            .Where(uid => uint.TryParse(uid, out uint value) && value <= position.LastUid && !existing.Contains(value))
            .ToArray();
        if (vanished.Length == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (string[] chunk in vanished.Chunk(500))
        {
            long[] ids = await _db.MailMessages
                .Where(m => m.SourceAccountId == accountId && m.RemoteFolder == folder && m.Storage == MessageStorage.Remote && m.RemoteUid != null && chunk.Contains(m.RemoteUid))
                .Select(m => m.Id)
                .ToArrayAsync(cancel);
            if (ids.Length > 0)
            {
                removed += await _store.DeleteAsync(ids, permanent: true, cancel);
            }
        }

        await ForgetAsync(run, folder, vanished, cancel);
        return removed;
    }

    private static string Megabytes(long? bytes) => bytes is long value ? $"{value / 1024.0 / 1024.0:0.#} MB" : "unknown size";
}

/// <summary>Dates of messages that arrive without an IMAP INTERNALDATE (POP3).</summary>
public static class MessageDates
{
    /// <summary>When the provider received the message: the newest Received header, else the Date header; null when neither can be read.</summary>
    public static DateTime? ReceivedDateOf(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            HeaderList headers = HeaderList.Load(stream);
            foreach (Header received in headers.Where(h => h.Id == HeaderId.Received))
            {
                int semicolon = received.Value.LastIndexOf(';');
                if (semicolon >= 0 && DateUtils.TryParse(received.Value[(semicolon + 1)..].Trim(), out DateTimeOffset date))
                {
                    return date.UtcDateTime;
                }
            }

            string? sent = headers[HeaderId.Date];
            return sent is not null && DateUtils.TryParse(sent, out DateTimeOffset sentDate) ? sentDate.UtcDateTime : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

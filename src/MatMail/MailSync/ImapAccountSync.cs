using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MatMail.Data;
using MatMail.Services;

namespace MatMail.MailSync;

/// <summary>
/// Synchronises an IMAP provider account. Per remote folder the new messages (UID above the stored position) are fetched in
/// batches and handed to the <see cref="SyncImporter"/>; after each committed batch the position moves on and, with
/// "delete after download", the batch is removed at the provider (never before it is stored locally). Then a bounded flag
/// comparison runs, and with live access the stand-ins of vanished messages are removed. Deleting a message locally never deletes
/// it at the provider.
/// </summary>
public sealed class ImapAccountSync : IAccountSync
{
    private const MessageSummaryItems NewMessageItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.Envelope;

    private const MessageSummaryItems LiveMessageItems = NewMessageItems | MessageSummaryItems.BodyStructure;

    private readonly ProviderConnector _connector;
    private readonly SyncImporter _importer;
    private readonly MailSyncOptions _options;
    private readonly ILogger<ImapAccountSync> _logger;

    public ImapAccountSync(ProviderConnector connector, SyncImporter importer, MailSyncOptions options, ILogger<ImapAccountSync> logger)
    {
        _connector = connector;
        _importer = importer;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(SyncRun run, CancellationToken cancel)
    {
        await _importer.PrepareAsync(run, cancel);
        using ImapClient client = await _connector.ConnectImapAsync(run.Account, cancel);

        foreach (IMailFolder folder in await SelectFoldersAsync(client, run, cancel))
        {
            if (run.Remaining == 0)
            {
                run.MorePending = true;
                break;
            }

            try
            {
                await SyncFolderAsync(client, folder, run, cancel);
                run.FoldersSynced++;
            }
            catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException or SyncFolderException)
            {
                run.FolderErrors.Add($"folder {folder.FullName}: {ex.Message}");
                _logger.LogWarning("Account {AccountId}: folder {Folder} could not be synchronised: {Reason}", run.Account.Id, folder.FullName, ex.Message);
            }
        }

        await client.DisconnectAsync(true, cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Folders
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Everyday mail: the configured folders (default INBOX), or with "all folders" the inbox and the custom folders. Backup and
    /// migration: every selectable folder. The inbox comes first, so it gets the share of a capped run.
    /// </summary>
    private static async Task<IReadOnlyList<IMailFolder>> SelectFoldersAsync(ImapClient client, SyncRun run, CancellationToken cancel)
    {
        if (run.Role == MailAccountRole.Mail && !run.Account.SyncAllFolders)
        {
            var configured = new List<IMailFolder>();
            foreach (string name in ConfiguredFolders(run.Account))
            {
                IMailFolder? folder = await FindFolderAsync(client, name, cancel);
                if (folder is null)
                {
                    run.FolderErrors.Add($"folder {name} does not exist at the provider");
                }
                else if (!configured.Any(f => f.FullName == folder.FullName))
                {
                    configured.Add(folder);
                }
            }

            return configured;
        }

        FolderNamespace personal = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        List<IMailFolder> folders = (await client.GetFoldersAsync(personal, StatusItems.None, false, cancel)).Where(f => f.CanOpen).ToList();
        if (!folders.Any(IsInbox))
        {
            folders.Add(client.Inbox);
        }

        Func<RemoteFolderInfo, bool> wanted = run.Role == MailAccountRole.Mail ? RemoteFolderMap.IsIncomingFolder : RemoteFolderMap.IsCopiedFolder;
        return folders
            .Where(f => wanted(RemoteFolderInfo.From(f)))
            .OrderBy(f => IsInbox(f) ? 0 : 1)
            .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ConfiguredFolders(MailAccount account)
    {
        string[] names = (account.SyncFolders ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? new[] { "INBOX" } : names;
    }

    /// <summary>The folder with this name; "/" in a configured name also matches the server's own separator (e.g. "INBOX.News").</summary>
    private static async Task<IMailFolder?> FindFolderAsync(ImapClient client, string name, CancellationToken cancel)
    {
        if (name.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return client.Inbox;
        }

        char separator = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0].DirectorySeparator : '/';
        foreach (string candidate in new[] { name, name.Replace('/', separator) }.Distinct())
        {
            try
            {
                return await client.GetFolderAsync(candidate, cancel);
            }
            catch (Exception ex) when (ex is FolderNotFoundException or ImapCommandException)
            {
                // try the next spelling
            }
        }

        return null;
    }

    private static bool IsInbox(IMailFolder folder) => folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase);

    private async Task SyncFolderAsync(ImapClient client, IMailFolder folder, SyncRun run, CancellationToken cancel)
    {
        var remote = RemoteFolderInfo.From(folder);
        await _importer.PrepareFolderAsync(run, remote, cancel);
        await folder.OpenAsync(run.DeletesAtProvider || run.SyncsFlags ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancel);
        FolderPosition position = await _importer.OpenPositionAsync(run, folder.FullName, folder.UidValidity, cancel);

        if (run.DeletesAtProvider && position.LastUid > 0)
        {
            await DeleteLeftoversAsync(client, folder, run, position, cancel);
        }

        bool caughtUp = await FetchNewMessagesAsync(client, folder, remote, run, position, cancel);

        // Downloading and the retention come first: a server that refuses the extras (e.g. a read-only folder) only gets a note.
        try
        {
            if (run.SyncsFlags && position.LastUid > 0)
            {
                await SyncFlagsAsync(folder, run, position, cancel);
            }

            if (run.IsLiveAccess && caughtUp)
            {
                await RemoveVanishedAsync(folder, run, position, cancel);
            }
        }
        catch (ImapCommandException ex)
        {
            run.Note($"flags of folder {folder.FullName} could not be compared ({ex.Message})");
            _logger.LogWarning("Account {AccountId}: flags of {Folder} could not be compared: {Reason}", run.Account.Id, folder.FullName, ex.Message);
        }

        await _importer.FinishFolderAsync(position, cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // New messages
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Imports the messages above the position, batch by batch. Returns false when the run's limit stopped it early.</summary>
    private async Task<bool> FetchNewMessagesAsync(
        ImapClient client, IMailFolder folder, RemoteFolderInfo remote, SyncRun run, FolderPosition position, CancellationToken cancel)
    {
        if (folder.Count == 0 || (folder.UidNext is UniqueId next && next.Id <= position.LastUid + 1))
        {
            return true;
        }

        var above = new UniqueIdRange(new UniqueId(folder.UidValidity, (uint)Math.Min(position.LastUid + 1, uint.MaxValue)), UniqueId.MaxValue);
        IList<UniqueId> found = await folder.SearchAsync(above, SearchQuery.All, cancel);
        var progress = new UidProgress(position.LastUid);

        foreach (uint[] batch in SyncBatches.NewUids(found.Select(u => u.Id), position.LastUid, _options.BatchSize))
        {
            if (run.Remaining == 0)
            {
                run.MorePending = true;
                return false;
            }

            Dictionary<string, long?> known = await _importer.KnownAsync(run, remote.FullName, batch.Select(Text).ToArray(), cancel);
            HashSet<uint> wanted = batch.Where(uid => !known.ContainsKey(Text(uid))).Take(run.Remaining).ToHashSet();
            Dictionary<uint, IMessageSummary> summaries = await FetchSummariesAsync(folder, wanted, run.IsLiveAccess, cancel);
            var stored = new List<UniqueId>();
            bool capped = false;

            foreach (uint uid in batch)
            {
                ImportResult result;
                if (known.TryGetValue(Text(uid), out long? localId))
                {
                    result = new ImportResult(ImportStatus.Known, localId);
                }
                else if (!wanted.Contains(uid))
                {
                    capped = true;
                    break;
                }
                else if (summaries.TryGetValue(uid, out IMessageSummary? summary))
                {
                    result = await ImportAsync(folder, remote, summary, run, cancel);
                }
                else
                {
                    result = new ImportResult(ImportStatus.Vanished);
                }

                if (result.IsDone)
                {
                    progress.Complete(uid);
                }
                else
                {
                    progress.Fail();
                }

                if (result.HasLocalCopy)
                {
                    stored.Add(new UniqueId(folder.UidValidity, uid));
                }
            }

            // Every message of the batch is stored and recorded (each in its own transaction): now the position may move on ...
            position.LastUid = progress.Position;
            await _importer.SavePositionAsync(position, cancel);

            // ... and only now may the originals go.
            if (run.DeletesAtProvider && stored.Count > 0)
            {
                await DeleteAtProviderAsync(client, folder, stored, run, cancel);
            }

            await _importer.HeartbeatAsync(run, cancel);
            if (capped)
            {
                run.MorePending = true;
                return false;
            }
        }

        return true;
    }

    private static async Task<Dictionary<uint, IMessageSummary>> FetchSummariesAsync(IMailFolder folder, IReadOnlyCollection<uint> uids, bool live, CancellationToken cancel)
    {
        if (uids.Count == 0)
        {
            return new Dictionary<uint, IMessageSummary>();
        }

        UniqueId[] ids = uids.Select(uid => new UniqueId(folder.UidValidity, uid)).ToArray();
        IList<IMessageSummary> summaries = live
            ? await folder.FetchAsync(ids, LiveMessageItems, LiveStub.HeaderFields, cancel)
            : await folder.FetchAsync(ids, NewMessageItems, cancel);
        return summaries
            .Where(s => s.UniqueId.IsValid)
            .GroupBy(s => s.UniqueId.Id)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private Task<ImportResult> ImportAsync(IMailFolder folder, RemoteFolderInfo remote, IMessageSummary summary, SyncRun run, CancellationToken cancel)
    {
        MessageFlags flags = summary.Flags ?? MessageFlags.None;
        var message = new RemoteMessage
        {
            Folder = remote,
            Uid = Text(summary.UniqueId.Id),
            Size = summary.Size,
            InternalDate = summary.InternalDate?.UtcDateTime,
            IsRead = flags.HasFlag(MessageFlags.Seen),
            IsStarred = flags.HasFlag(MessageFlags.Flagged),
            IsAnswered = flags.HasFlag(MessageFlags.Answered),
            IsDraft = flags.HasFlag(MessageFlags.Draft),
            Keywords = summary.Keywords is { Count: > 0 } keywords ? keywords.ToArray() : null,
            MessageId = NormalizeMessageId(summary.Envelope?.MessageId),
            Stub = run.IsLiveAccess ? LiveStub.Build(summary.Headers) : null,
            HasAttachments = run.IsLiveAccess && summary.Body is not null && summary.Attachments.Any(),
        };

        UniqueId uid = summary.UniqueId;
        return _importer.ImportAsync(run, message, token => DownloadAsync(folder, uid, token), cancel);
    }

    private static async Task<byte[]> DownloadAsync(IMailFolder folder, UniqueId uid, CancellationToken cancel)
    {
        using Stream stream = await folder.GetStreamAsync(uid, cancel);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Deleting at the provider ("delete after download")
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task DeleteAtProviderAsync(ImapClient client, IMailFolder folder, IList<UniqueId> uids, SyncRun run, CancellationToken cancel)
    {
        await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, cancel);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await folder.ExpungeAsync(uids, cancel);
        }
        else
        {
            // Without UIDPLUS there is only the plain EXPUNGE, which also removes what other clients marked as deleted.
            await folder.ExpungeAsync(cancel);
        }

        run.DeletedAtProvider += uids.Count;
    }

    /// <summary>
    /// Originals that are stored locally but still at the provider (an earlier deletion was interrupted) are removed now; records of
    /// messages that are gone are dropped, so they do not pile up.
    /// </summary>
    private async Task DeleteLeftoversAsync(ImapClient client, IMailFolder folder, SyncRun run, FolderPosition position, CancellationToken cancel)
    {
        IList<UniqueId> existing = folder.Count == 0
            ? Array.Empty<UniqueId>()
            : await folder.SearchAsync(new UniqueIdRange(folder.UidValidity, 1, (uint)Math.Min(position.LastUid, uint.MaxValue)), SearchQuery.All, cancel);
        Dictionary<string, long?> known = await _importer.KnownAsync(run, position.RemoteFolder, null, cancel);

        List<UniqueId> leftovers = existing.Where(u => known.TryGetValue(Text(u.Id), out long? localId) && localId is not null).ToList();
        if (leftovers.Count > 0)
        {
            await DeleteAtProviderAsync(client, folder, leftovers, run, cancel);
        }

        HashSet<uint> remaining = existing.Select(u => u.Id).Except(leftovers.Select(u => u.Id)).ToHashSet();
        string[] gone = known.Keys
            .Where(uid => uint.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) && value <= position.LastUid && !remaining.Contains(value))
            .ToArray();
        await _importer.ForgetAsync(run, position.RemoteFolder, gone, cancel);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Flags and vanished messages
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Read/starred of the most recent messages: remote changes are pulled, local changes are pushed.</summary>
    private async Task SyncFlagsAsync(IMailFolder folder, SyncRun run, FolderPosition position, CancellationToken cancel)
    {
        if (folder.Count == 0)
        {
            return;
        }

        int first = Math.Max(0, folder.Count - _options.FlagSyncWindow);
        IList<IMessageSummary> summaries = await folder.FetchAsync(first, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancel);
        Dictionary<string, RemoteFlags> remote = summaries
            .Where(s => s.UniqueId.IsValid && s.UniqueId.Id <= position.LastUid && s.Flags is not null)
            .GroupBy(s => s.UniqueId.Id)
            .ToDictionary(g => Text(g.Key), g => ToRemoteFlags(g.First().Flags!.Value), StringComparer.Ordinal);

        (IReadOnlyList<FlagPush> push, DateTime readAt) = await _importer.ReconcileFlagsAsync(run, position.RemoteFolder, remote, cancel);
        if (push.Count == 0)
        {
            return;
        }

        await StoreFlagAsync(folder, push.Where(p => p.Seen && !p.Remote.Seen), MessageFlags.Seen, true, cancel);
        await StoreFlagAsync(folder, push.Where(p => !p.Seen && p.Remote.Seen), MessageFlags.Seen, false, cancel);
        await StoreFlagAsync(folder, push.Where(p => p.Flagged && !p.Remote.Flagged), MessageFlags.Flagged, true, cancel);
        await StoreFlagAsync(folder, push.Where(p => !p.Flagged && p.Remote.Flagged), MessageFlags.Flagged, false, cancel);
        await _importer.MarkReconciledAsync(run, position.RemoteFolder, push.Select(p => p.RemoteUid).ToArray(), readAt, cancel);
        run.FlagChanges += push.Count;
    }

    private static async Task StoreFlagAsync(IMailFolder folder, IEnumerable<FlagPush> pushes, MessageFlags flag, bool add, CancellationToken cancel)
    {
        UniqueId[] uids = pushes.Select(p => new UniqueId(folder.UidValidity, uint.Parse(p.RemoteUid, CultureInfo.InvariantCulture))).ToArray();
        if (uids.Length == 0)
        {
            return;
        }

        if (add)
        {
            await folder.AddFlagsAsync(uids, flag, true, cancel);
        }
        else
        {
            await folder.RemoveFlagsAsync(uids, flag, true, cancel);
        }
    }

    /// <summary>Live access: the stand-ins of messages that are gone at the provider are removed (cheap count check first).</summary>
    private async Task RemoveVanishedAsync(IMailFolder folder, SyncRun run, FolderPosition position, CancellationToken cancel)
    {
        int recorded = await _importer.CountKnownAsync(run, position.RemoteFolder, cancel);
        if (recorded <= folder.Count)
        {
            return;
        }

        IList<UniqueId> existing = await folder.SearchAsync(SearchQuery.All, cancel);
        if (existing.Count != folder.Count)
        {
            // The folder changed while we looked; the next run checks again.
            return;
        }

        run.RemovedLocally += await _importer.RemoveVanishedAsync(run, position, existing.Select(u => u.Id).ToHashSet(), cancel);
    }

    private static RemoteFlags ToRemoteFlags(MessageFlags flags) => new(flags.HasFlag(MessageFlags.Seen), flags.HasFlag(MessageFlags.Flagged));

    private static string Text(uint uid) => uid.ToString(CultureInfo.InvariantCulture);

    /// <summary>"&lt;id@host&gt;" like the mail store keeps it.</summary>
    internal static string? NormalizeMessageId(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : "<" + id.Trim().Trim('<', '>') + ">";
}

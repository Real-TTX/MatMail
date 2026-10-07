using MailKit.Net.Pop3;
using MatMail.Services;

namespace MatMail.MailSync;

/// <summary>
/// Synchronises a POP3 provider account: the UIDL list is compared with what was recorded (folder "INBOX", UID = UIDL), new
/// messages are downloaded (up to the size limit) and stored like IMAP messages of the same role. With "delete after download"
/// the stored messages are marked with DELE after the local commit; POP3 removes them at the end of the session (QUIT), so an
/// interrupted session deletes nothing and the next run catches up. POP3 knows no flags and no live access.
/// </summary>
public sealed class Pop3AccountSync : IAccountSync
{
    private readonly ProviderConnector _connector;
    private readonly SyncImporter _importer;
    private readonly MailSyncOptions _options;

    public Pop3AccountSync(ProviderConnector connector, SyncImporter importer, MailSyncOptions options)
    {
        _connector = connector;
        _importer = importer;
        _options = options;
    }

    public async Task RunAsync(SyncRun run, CancellationToken cancel)
    {
        RemoteFolderInfo inbox = RemoteFolderInfo.Pop3Inbox;
        await _importer.PrepareAsync(run, cancel);
        await _importer.PrepareFolderAsync(run, inbox, cancel);
        using Pop3Client client = await _connector.ConnectPop3Async(run.Account, cancel);

        IList<string> uids;
        try
        {
            uids = await client.GetMessageUidsAsync(cancel);
        }
        catch (NotSupportedException)
        {
            throw new SyncConfigurationException($"{run.Account.ReceiveHost}: the POP3 server does not support UIDL, so fetched messages cannot be recognised; use IMAP instead.");
        }

        IList<int> sizes = uids.Count == 0 ? Array.Empty<int>() : await client.GetMessageSizesAsync(cancel);
        Dictionary<string, long?> known = await _importer.KnownAsync(run, inbox.FullName, null, cancel);
        var toDelete = new List<int>();
        var deletedUids = new List<string>();
        int sinceHeartbeat = 0;

        for (int index = 0; index < uids.Count; index++)
        {
            string uid = uids[index];
            ImportResult result;
            if (known.TryGetValue(uid, out long? localId))
            {
                result = new ImportResult(ImportStatus.Known, localId);
            }
            else if (run.Remaining == 0)
            {
                run.MorePending = true;
                break;
            }
            else
            {
                int messageIndex = index;
                var remote = new RemoteMessage { Folder = inbox, Uid = uid, Size = index < sizes.Count ? sizes[index] : null };
                result = await _importer.ImportAsync(run, remote, token => DownloadAsync(client, messageIndex, token), cancel);
                if (++sinceHeartbeat >= _options.BatchSize)
                {
                    await _importer.HeartbeatAsync(run, cancel);
                    sinceHeartbeat = 0;
                }
            }

            // Stored locally (in this or an earlier run): with "delete after download" the original may go now.
            if (run.DeletesAtProvider && result.HasLocalCopy)
            {
                toDelete.Add(index);
                deletedUids.Add(uid);
            }
        }

        if (toDelete.Count > 0)
        {
            await client.DeleteMessagesAsync(toDelete, cancel);
        }

        // QUIT: only now does the server remove the messages marked with DELE.
        await client.DisconnectAsync(true, cancel);
        run.DeletedAtProvider += toDelete.Count;
        await _importer.ForgetAsync(run, inbox.FullName, deletedUids, cancel);
        run.FoldersSynced++;
    }

    private static async Task<byte[]> DownloadAsync(Pop3Client client, int index, CancellationToken cancel)
    {
        using Stream stream = await client.GetStreamAsync(index, false, cancel);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }
}

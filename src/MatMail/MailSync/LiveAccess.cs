using System.Collections.Concurrent;
using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MailStore = MatMail.Messaging.MailStore;

namespace MatMail.MailSync;

/// <summary>
/// Live access stores no message bodies, only a header-only stand-in, so lists, search and the IMAP/web listings work. It is built
/// from a few header fields fetched from the provider (including the Delivered-To family, so catch-all routing still works).
/// </summary>
public static class LiveStub
{
    /// <summary>The header fields fetched for a stand-in.</summary>
    public static readonly string[] HeaderFields =
    {
        "From", "Sender", "Reply-To", "To", "Cc", "Subject", "Date", "Message-ID", "In-Reply-To", "References",
        "MIME-Version", "Content-Type", "Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To", "X-Delivered-To",
        "List-Id", "Importance", "X-Priority",
    };

    /// <summary>A header-only RFC 822 message (the wanted header fields, a blank line, no body).</summary>
    public static byte[] Build(HeaderList? headers)
    {
        var kept = new HeaderList();
        foreach (Header header in headers ?? new HeaderList())
        {
            if (HeaderFields.Contains(header.Field, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(header.Clone());
            }
        }

        FormatOptions format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        using var stream = new MemoryStream();
        kept.WriteTo(format, stream);
        stream.Write("\r\n"u8);
        return stream.ToArray();
    }
}

/// <summary>
/// Fetches the bodies of live-access messages on demand (<see cref="MailStore.GetRawAsync"/> asks it for messages with
/// <see cref="MessageStorage.Remote"/>): it connects to the provider (connections are kept for a short while and reused), opens the
/// remote folder, checks that the folder was not renumbered (UIDVALIDITY) and fetches the message by UID. Recently fetched
/// messages are kept in memory for a moment, because clients often read the same message several times in a row; nothing is
/// written to the database, in the spirit of live access.
/// </summary>
public sealed class RemoteContentFetcher : IRemoteContentProvider, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ProviderConnector _connector;
    private readonly MailSyncOptions _options;
    private readonly ILogger<RemoteContentFetcher> _logger;
    private readonly ConcurrentDictionary<long, LiveConnection> _connections = new();
    private readonly LiveMessageCache _cache;
    private readonly Timer _janitor;

    public RemoteContentFetcher(IServiceScopeFactory scopes, SecretProtector secrets, MailSyncOptions options, ILogger<RemoteContentFetcher> logger)
    {
        _scopes = scopes;
        _connector = new ProviderConnector(secrets);
        _options = options;
        _logger = logger;
        _cache = new LiveMessageCache(options.LiveCacheBytes, options.LiveCacheLifetime);
        TimeSpan period = TimeSpan.FromSeconds(Math.Clamp(options.LiveConnectionIdle.TotalSeconds / 2, 1, 30));
        _janitor = new Timer(_ => CloseIdleConnections(), null, period, period);
    }

    public async Task<byte[]?> FetchAsync(MailMessage message, CancellationToken cancel)
    {
        if (message.Storage != MessageStorage.Remote || message.SourceAccountId is not long accountId || string.IsNullOrEmpty(message.RemoteFolder)
            || !uint.TryParse(message.RemoteUid, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) || uid == 0)
        {
            return null;
        }

        if (_cache.TryGet(message.Id, out byte[]? cached))
        {
            return cached;
        }

        LiveSource? source = await LoadSourceAsync(message, accountId, cancel);
        if (source is null)
        {
            return null;
        }

        LiveConnection connection = _connections.GetOrAdd(accountId, _ => new LiveConnection());
        await connection.Gate.WaitAsync(cancel);
        try
        {
            byte[]? raw = await FetchWithRetryAsync(connection, source, message, uid, cancel);
            if (raw is not null)
            {
                _cache.Add(message.Id, raw);
            }

            return raw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _logger.LogWarning("Message {MessageId} could not be fetched from the provider: {Reason}", message.Id, ProviderConnector.Describe(ex, source.Account.ReceiveHost));
            connection.Reset();
            return null;
        }
        finally
        {
            connection.LastUsed = DateTime.UtcNow;
            connection.Gate.Release();
        }
    }

    public void Dispose()
    {
        _janitor.Dispose();
        foreach (LiveConnection connection in _connections.Values)
        {
            connection.Reset();
        }

        _connections.Clear();
    }

    private sealed record LiveSource(MailAccount Account, uint UidValidity);

    /// <summary>The account (enabled IMAP account of the message's tenant) and the UIDVALIDITY the message's UID belongs to.</summary>
    private async Task<LiveSource?> LoadSourceAsync(MailMessage message, long accountId, CancellationToken cancel)
    {
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystemInTenant(message.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        MailAccount? account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.TenantId == message.TenantId, cancel);
        if (account is not { IsEnabled: true, ReceiveProtocol: ReceiveProtocol.Imap } || string.IsNullOrWhiteSpace(account.ReceiveHost))
        {
            return null;
        }

        string folder = message.RemoteFolder!;
        long? uidValidity = await db.MailAccountFolderStates.AsNoTracking()
            .Where(s => s.MailAccountId == accountId && s.RemoteFolder == folder)
            .Select(s => (long?)s.UidValidity)
            .FirstOrDefaultAsync(cancel);
        return uidValidity is long validity ? new LiveSource(account, (uint)validity) : null;
    }

    private async Task<byte[]?> FetchWithRetryAsync(LiveConnection connection, LiveSource source, MailMessage message, uint uid, CancellationToken cancel)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await FetchOnceAsync(connection, source, message, uid, cancel);
            }
            catch (MessageNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (attempt == 1 && !SyncImporter.IsMessageProblem(ex) && !cancel.IsCancellationRequested)
            {
                // The kept connection may have been closed by the provider: once more with a fresh one.
                connection.Reset();
            }
        }
    }

    private async Task<byte[]?> FetchOnceAsync(LiveConnection connection, LiveSource source, MailMessage message, uint uid, CancellationToken cancel)
    {
        await connection.ConnectAsync(_connector, source.Account, cancel);
        IMailFolder folder = await connection.OpenFolderAsync(message.RemoteFolder!, cancel);
        if (folder.UidValidity != source.UidValidity)
        {
            // Renumbered since the last synchronisation: the UID may mean another message now. The next run lists the folder afresh.
            _logger.LogInformation("Message {MessageId}: folder {Folder} was renumbered at the provider; not fetched.", message.Id, message.RemoteFolder);
            return null;
        }

        using Stream stream = await folder.GetStreamAsync(new UniqueId(source.UidValidity, uid), cancel);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancel);
        byte[] raw = buffer.ToArray();

        if (!HasMessageId(raw, message.MessageIdHeader))
        {
            _logger.LogWarning("Message {MessageId}: the provider returned a different message for UID {Uid} in {Folder}; not used.", message.Id, uid, message.RemoteFolder);
            return null;
        }

        return raw;
    }

    /// <summary>A last safety check: the fetched message must carry the Message-ID of the stand-in (when both have one).</summary>
    private static bool HasMessageId(byte[] raw, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            string? actual = ImapAccountSync.NormalizeMessageId(HeaderList.Load(stream)[HeaderId.MessageId]);
            return actual is null || string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private void CloseIdleConnections()
    {
        DateTime idleBefore = DateTime.UtcNow - _options.LiveConnectionIdle;
        foreach (LiveConnection connection in _connections.Values)
        {
            if (connection.LastUsed >= idleBefore || !connection.Gate.Wait(0))
            {
                continue;
            }

            try
            {
                connection.Reset();
            }
            finally
            {
                connection.Gate.Release();
            }
        }
    }

    /// <summary>One kept provider connection per account; <see cref="Gate"/> lets one fetch at a time use it.</summary>
    private sealed class LiveConnection
    {
        private ImapClient? _client;
        private string? _settings;
        private IMailFolder? _folder;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public async Task ConnectAsync(ProviderConnector connector, MailAccount account, CancellationToken cancel)
        {
            // Changed connection settings (host, user, password, ...) need a new connection.
            string settings = string.Join('|', account.ReceiveHost, account.ReceivePort, account.ReceiveSecurity, account.ReceiveUsername, account.ReceivePasswordProtected, account.AllowInvalidCertificate);
            if (_client is { IsConnected: true, IsAuthenticated: true } && _settings == settings)
            {
                return;
            }

            Reset();
            _client = await connector.ConnectImapAsync(account, cancel);
            _settings = settings;
        }

        public async Task<IMailFolder> OpenFolderAsync(string name, CancellationToken cancel)
        {
            if (_folder is { IsOpen: true } && _folder.FullName == name)
            {
                return _folder;
            }

            ImapClient client = _client ?? throw new InvalidOperationException("Not connected.");
            IMailFolder folder = name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(name, cancel);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancel);
            _folder = folder;
            return folder;
        }

        public void Reset()
        {
            ImapClient? client = _client;
            _client = null;
            _folder = null;
            _settings = null;
            try
            {
                client?.Dispose();
            }
            catch (Exception)
            {
                // closing a broken connection must not fail
            }
        }
    }
}

/// <summary>A small in-memory cache of fetched message bytes: bounded in size, entries expire, least recently used go first.</summary>
public sealed class LiveMessageCache
{
    private readonly object _lock = new();
    private readonly long _maxBytes;
    private readonly TimeSpan _lifetime;
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _byId = new();
    private long _bytes;

    public LiveMessageCache(long maxBytes, TimeSpan lifetime)
    {
        _maxBytes = maxBytes;
        _lifetime = lifetime;
    }

    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    public bool TryGet(long messageId, out byte[]? raw)
    {
        lock (_lock)
        {
            raw = null;
            if (!_byId.TryGetValue(messageId, out LinkedListNode<Entry>? node))
            {
                return false;
            }

            if (node.Value.Expires < DateTime.UtcNow)
            {
                Remove(node);
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            raw = node.Value.Raw;
            return true;
        }
    }

    /// <summary>Keeps the bytes; messages larger than a quarter of the cache are not kept at all.</summary>
    public void Add(long messageId, byte[] raw)
    {
        if (raw.LongLength > _maxBytes / 4)
        {
            return;
        }

        lock (_lock)
        {
            if (_byId.TryGetValue(messageId, out LinkedListNode<Entry>? existing))
            {
                Remove(existing);
            }

            _byId[messageId] = _order.AddFirst(new Entry(messageId, raw, DateTime.UtcNow + _lifetime));
            _bytes += raw.LongLength;
            while (_bytes > _maxBytes && _order.Last is { } oldest)
            {
                Remove(oldest);
            }
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _byId.Remove(node.Value.MessageId);
        _bytes -= node.Value.Raw.LongLength;
    }

    private sealed record Entry(long MessageId, byte[] Raw, DateTime Expires);
}

using System.Net.Sockets;
using System.Security.Authentication;
using MatMail.Data;
using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailServer.Imap;

internal enum ImapSessionState
{
    NotAuthenticated,
    Authenticated,
    Selected,
    Logout,
}

/// <summary>A parsed command line: tag, name, whether it came with the UID prefix, and the parser positioned after the name.</summary>
internal sealed class ImapCommand
{
    public required string Tag { get; init; }

    public required string Name { get; init; }

    public bool IsUid { get; init; }

    public required ImapParser Parser { get; init; }

    /// <summary>"UID FETCH" or "FETCH", for response texts.</summary>
    public string DisplayName => IsUid ? "UID " + Name : Name;
}

/// <summary>A command that fails with a tagged NO; the text may start with a response code ("[NONEXISTENT] ...").</summary>
internal sealed class ImapNoException : Exception
{
    public ImapNoException(string text) : base(text)
    {
    }
}

/// <summary>
/// One IMAP4rev1 session (RFC 3501): reads commands (pipelined commands are processed in order), answers them and keeps the
/// selected folder's snapshot in step with changes made elsewhere (other sessions, the web client, delivery).
/// </summary>
internal sealed partial class ImapSession
{
    private static readonly Dictionary<string, CommandHandler> Handlers = CreateHandlers();
    private static readonly HashSet<string> UidCommands = new(StringComparer.OrdinalIgnoreCase) { "FETCH", "STORE", "COPY", "MOVE", "SEARCH", "EXPUNGE" };
    private static readonly TimeSpan PreAuthenticationTimeout = TimeSpan.FromMinutes(2);

    private readonly ImapConnection _connection;
    private readonly ImapRequestReader _reader;
    private readonly ImapServerContext _context;
    private readonly IServiceProvider _connectionServices;
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ImapMessageCache _messageCache = new();

    private ImapSessionState _state = ImapSessionState.NotAuthenticated;
    private MailUser? _user;
    private DateTime _accessCheckedAt = DateTime.MinValue;
    private volatile ImapSelection? _selection;
    private IDisposable? _subscription;
    private int _changePending;
    private int _failedLogins;

    public ImapSession(ImapConnection connection, ImapServerContext context, IServiceProvider connectionServices, CancellationToken shutdown)
    {
        _connection = connection;
        _reader = new ImapRequestReader(connection);
        _context = context;
        _connectionServices = connectionServices;
        _shutdown = shutdown;
    }

    [Flags]
    private enum States
    {
        NotAuthenticated = 1,
        Authenticated = 2,
        Selected = 4,
        LoggedIn = Authenticated | Selected,
        Any = NotAuthenticated | Authenticated | Selected,
    }

    private bool IsClosing => _state == ImapSessionState.Logout;

    /// <summary>Runs the session until the client logs out or disconnects, the idle timeout passes, or the server shuts down.</summary>
    public async Task RunAsync()
    {
        try
        {
            WriteUntagged($"OK [CAPABILITY {Capabilities()}] {_context.Config.Server.Hostname} MatMail IMAP4rev1 server ready");
            await _connection.FlushAsync(_shutdown);

            while (!IsClosing)
            {
                ImapRequest? request = await ReadRequestAsync();
                if (request is null)
                {
                    break;
                }

                await ExecuteAsync(request);
                await _connection.FlushAsync(_shutdown);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            await SayGoodbyeAsync("BYE Server shutting down");
        }
        catch (ImapLineTooLongException)
        {
            await SayGoodbyeAsync("BYE Command line too long");
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            _context.Logger.LogDebug(ex, "IMAP connection from {RemoteIp} ended.", _connection.RemoteIp);
        }
        finally
        {
            _subscription?.Dispose();
            _state = ImapSessionState.Logout;
        }
    }

    /// <summary>
    /// Reads the next command; null when the client disconnected or the session timed out. Before sign-in the timeout is short (a
    /// client signs in right away); afterwards it is the full autologout time (RFC 3501, section 5.4: at least 30 minutes).
    /// </summary>
    private async Task<ImapRequest?> ReadRequestAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        timeout.CancelAfter(_state == ImapSessionState.NotAuthenticated ? Min(_context.IdleTimeout, PreAuthenticationTimeout) : _context.IdleTimeout);
        try
        {
            return await _reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            await SayGoodbyeAsync("BYE Autologout; idle for too long");
            return null;
        }
    }

    private async Task ExecuteAsync(ImapRequest request)
    {
        if (request.Rejection is { } rejection)
        {
            WriteLine($"{request.TagOrStar} {rejection.Status} {rejection.Text}");
            return;
        }

        var parser = new ImapParser(request);
        string tag;
        string name;
        try
        {
            tag = parser.ReadTag();
            parser.ExpectSpace();
            name = parser.ReadAtom().ToUpperInvariant();
        }
        catch (ImapSyntaxException)
        {
            string fallbackTag = request.TagOrStar;
            WriteLine(fallbackTag == "*" ? "* BAD Invalid command line" : $"{fallbackTag} BAD Missing command name");
            return;
        }

        bool isUid = false;
        if (name == "UID")
        {
            isUid = true;
            name = TryReadUidCommand(parser) ?? string.Empty;
            if (!UidCommands.Contains(name))
            {
                WriteLine($"{tag} BAD Unknown UID command");
                return;
            }
        }

        if (!Handlers.TryGetValue(name, out CommandHandler? handler))
        {
            WriteLine($"{tag} BAD Unknown command {Truncate(name)}");
            return;
        }

        if (!handler.States.HasFlag(CurrentStateFlag()))
        {
            WriteLine($"{tag} BAD {StateError(handler.States)}");
            return;
        }

        if (_state is ImapSessionState.Authenticated or ImapSessionState.Selected && !await StillAllowedAsync())
        {
            return;
        }

        var command = new ImapCommand { Tag = tag, Name = name, IsUid = isUid, Parser = parser };
        await RunHandlerAsync(handler, command);
    }

    /// <summary>
    /// A session can stay open for days, so what an administrator takes away has to reach it: a deactivated user, a role without
    /// mail rights, a withdrawn or reduced delegation. Looked up every few seconds (and while idling); false = the session was ended.
    /// </summary>
    private async Task<bool> StillAllowedAsync()
    {
        MailUser? user = _user;
        DateTime now = DateTime.UtcNow;
        if (user is null || now - _accessCheckedAt < _context.AccessRecheckInterval)
        {
            return true;
        }

        _accessCheckedAt = now;
        await using ImapWork work = OpenWork();
        MailUser? fresh = await work.Access.RefreshAsync(user, _shutdown);
        if (fresh is null)
        {
            _selection = null;
            await SayGoodbyeAsync("BYE This account may no longer use the mail server");
            return false;
        }

        _user = fresh;
        ImapSelection? selection = _selection;
        if (selection is null)
        {
            return true;
        }

        IReadOnlyList<AccessibleMailbox> mailboxes = await work.Access.GetMailboxesAsync(fresh, _shutdown);
        AccessibleMailbox? mailbox = mailboxes.FirstOrDefault(m => m.Mailbox.Id == selection.MailboxId);
        if (mailbox is null)
        {
            _selection = null;
            await SayGoodbyeAsync("BYE Access to the selected mailbox was withdrawn");
            return false;
        }

        selection.Access = mailbox.Access;
        return true;
    }

    private async Task RunHandlerAsync(CommandHandler handler, ImapCommand command)
    {
        try
        {
            await handler.Execute(this, command);
        }
        catch (ImapSyntaxException ex)
        {
            Tagged(command, "BAD", ex.Message);
        }
        catch (ImapNoException ex)
        {
            Tagged(command, "NO", ex.Message);
        }
        catch (Exception ex) when (!IsConnectionError(ex) && ex is not ImapLineTooLongException && !(ex is OperationCanceledException && _shutdown.IsCancellationRequested))
        {
            _context.Logger.LogError(ex, "IMAP command {Command} of {User} failed.", command.DisplayName, _user?.LoginName ?? "(not signed in)");
            Tagged(command, "NO", "[SERVERBUG] The command failed on the server");
        }
    }

    private static string? TryReadUidCommand(ImapParser parser)
    {
        try
        {
            parser.ExpectSpace();
            return parser.ReadAtom().ToUpperInvariant();
        }
        catch (ImapSyntaxException)
        {
            return null;
        }
    }

    private States CurrentStateFlag() => _state switch
    {
        ImapSessionState.NotAuthenticated => States.NotAuthenticated,
        ImapSessionState.Authenticated => States.Authenticated,
        ImapSessionState.Selected => States.Selected,
        _ => 0,
    };

    private string StateError(States allowed)
    {
        if (_state == ImapSessionState.NotAuthenticated)
        {
            return "Please log in first";
        }

        if (allowed == States.NotAuthenticated)
        {
            return "Already logged in";
        }

        return allowed == States.Selected ? "No mailbox selected" : "Not allowed while a mailbox is selected";
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Responses
    // ---------------------------------------------------------------------------------------------------------------

    private void WriteLine(string line) => _connection.Write(line + "\r\n");

    private void WriteUntagged(string text) => WriteLine("* " + text);

    private void Tagged(ImapCommand command, string status, string text) => WriteLine($"{command.Tag} {status} {text}");

    /// <summary>Tells the client about changes of the selected folder, then completes the command with OK.</summary>
    private async Task CompleteAsync(ImapCommand command, string text, ImapWork? work = null, bool allowExpunge = true)
    {
        await SynchronizeAsync(work, allowExpunge);
        if (!IsClosing)
        {
            Tagged(command, "OK", text);
        }
    }

    private async Task SayGoodbyeAsync(string text)
    {
        _state = ImapSessionState.Logout;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            WriteUntagged(text);
            await _connection.FlushAsync(timeout.Token);
        }
        catch (Exception ex) when (IsConnectionError(ex) || ex is OperationCanceledException)
        {
            // The client is gone or does not read; nothing more to say.
        }
    }

    private ImapWork OpenWork() => new(_context.Scopes, _user);

    private ImapSelection RequireSelection() => _selection ?? throw new ImapSyntaxException("No mailbox selected");

    private MailUser User => _user ?? throw new InvalidOperationException("Not authenticated.");

    private static bool IsConnectionError(Exception ex)
        => ex is IOException or SocketException or ObjectDisposedException or AuthenticationException or EndOfStreamException;

    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40];

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // ---------------------------------------------------------------------------------------------------------------
    // Keeping the selected folder in step
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Listens to the event hub (from login on); changes of the selected folder wake IDLE and mark the snapshot stale.</summary>
    private void ListenForChanges() => _subscription ??= _context.Hub.Subscribe(OnMailEvent);

    private void OnMailEvent(MailEvent mailEvent)
    {
        MailUser? user = _user;
        ImapSelection? selection = _selection;
        if (user is null || selection is null || mailEvent.TenantId != user.TenantId || mailEvent.FolderId != selection.FolderId)
        {
            return;
        }

        Interlocked.Exchange(ref _changePending, 1);
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>
    /// Brings the snapshot of the selected folder up to date and reports the differences: EXPUNGE (only where the protocol allows
    /// it), EXISTS for new mail, FLAGS for new keywords, and FETCH for flags changed elsewhere.
    /// </summary>
    private async Task SynchronizeAsync(ImapWork? work, bool allowExpunge)
    {
        ImapSelection? selection = _selection;
        if (selection is null || IsClosing)
        {
            return;
        }

        if (work is not null)
        {
            await RefreshAsync(work, selection);
        }
        else
        {
            await using ImapWork own = OpenWork();
            await RefreshAsync(own, selection);
        }

        if (!IsClosing)
        {
            ReportChanges(selection, allowExpunge);
        }
    }

    private async Task RefreshAsync(ImapWork work, ImapSelection selection)
    {
        var folder = await work.Db.MailFolders.AsNoTracking()
            .Where(f => f.Id == selection.FolderId)
            .Select(f => new { f.ModSeq, f.UidValidity })
            .FirstOrDefaultAsync(_shutdown);
        if (folder is null || folder.UidValidity != selection.UidValidity)
        {
            // Deleted (or rebuilt) by somebody else: the client's view cannot be repaired.
            _selection = null;
            await SayGoodbyeAsync("BYE The selected mailbox no longer exists");
            return;
        }

        bool pending = Interlocked.Exchange(ref _changePending, 0) == 1;
        if (!pending && folder.ModSeq == selection.KnownModSeq)
        {
            return;
        }

        List<ImapMessageRow> rows = await LoadRowsAsync(work, selection.FolderId);
        selection.KnownModSeq = folder.ModSeq;
        MergeRows(selection, rows);
    }

    /// <summary>The flag columns of every message of a folder in UID order (never the message bodies).</summary>
    private Task<List<ImapMessageRow>> LoadRowsAsync(ImapWork work, long folderId)
        => work.Db.MailMessages.AsNoTracking()
            .Where(m => m.FolderId == folderId)
            .OrderBy(m => m.Uid)
            .Select(m => new ImapMessageRow(m.Id, m.Uid, m.IsRead, m.IsAnswered, m.IsStarred, m.IsDeleted, m.IsDraft, m.IsForwarded, m.Keywords))
            .ToListAsync(_shutdown);

    /// <summary>Compares the database with the snapshot (both in UID order): gone messages, new ones, changed flags.</summary>
    private static void MergeRows(ImapSelection selection, List<ImapMessageRow> rows)
    {
        long knownMaxUid = selection.MaxUid;
        var added = new List<ImapMessage>();
        int index = 0;
        int row = 0;

        while (index < selection.Messages.Count || row < rows.Count)
        {
            ImapMessage? message = index < selection.Messages.Count ? selection.Messages[index] : null;
            ImapMessageRow? current = row < rows.Count ? rows[row] : null;

            if (current is null || (message is not null && message.Uid < current.Uid))
            {
                message!.IsExpunged = true;
                index++;
            }
            else if (message is null || current.Uid < message.Uid)
            {
                if (current.Uid > knownMaxUid)
                {
                    added.Add(current.ToMessage());
                }

                row++;
            }
            else
            {
                (ImapFlags flags, string[] keywords) = current.Flags();
                if (!message.IsExpunged && !message.HasSameFlags(flags, keywords))
                {
                    message.Flags = flags;
                    message.Keywords = keywords;
                    selection.ChangedFlags.Add(message);
                }

                index++;
                row++;
            }
        }

        selection.Messages.AddRange(added);
    }

    private void ReportChanges(ImapSelection selection, bool allowExpunge)
    {
        if (allowExpunge && selection.Messages.Any(m => m.IsExpunged))
        {
            for (int index = selection.Messages.Count - 1; index >= 0; index--)
            {
                if (selection.Messages[index].IsExpunged)
                {
                    WriteUntagged($"{index + 1} EXPUNGE");
                    selection.ClientCount--;
                }
            }

            selection.Messages.RemoveAll(m => m.IsExpunged);
        }

        if (selection.Count != selection.ClientCount)
        {
            WriteUntagged($"{selection.Count} EXISTS");
            selection.ClientCount = selection.Count;
        }

        ReportNewKeywords(selection);

        foreach (ImapMessage message in selection.ChangedFlags.Where(m => !m.IsExpunged).OrderBy(m => m.Uid))
        {
            int index = selection.IndexOfUid(message.Uid);
            if (index >= 0)
            {
                WriteUntagged($"{index + 1} FETCH (UID {message.Uid} FLAGS {ImapFlagNames.Format(message.Flags, message.Keywords)})");
            }
        }

        selection.ChangedFlags.Clear();
    }

    /// <summary>Announces keywords that appeared in the folder since the FLAGS response (RFC 3501 allows FLAGS at any time).</summary>
    private void ReportNewKeywords(ImapSelection selection)
    {
        bool changed = false;
        foreach (string keyword in selection.Messages.SelectMany(m => m.Keywords))
        {
            changed |= selection.Keywords.Add(keyword);
        }

        if (changed)
        {
            WriteFlagResponses(selection);
        }
    }

    private void WriteFlagResponses(ImapSelection selection)
    {
        string defined = string.Join(' ', ImapFlagNames.Known.Select(k => k.Name).Concat(selection.Keywords.Order(StringComparer.OrdinalIgnoreCase)));
        WriteUntagged($"FLAGS ({defined})");
        WriteUntagged(selection.IsReadOnly
            ? "OK [PERMANENTFLAGS ()] No permanent flags permitted"
            : $"OK [PERMANENTFLAGS ({defined} \\*)] Flags permitted");
    }

    private sealed record CommandHandler(States States, Func<ImapSession, ImapCommand, Task> Execute);

    private static Dictionary<string, CommandHandler> CreateHandlers() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Any state
        ["CAPABILITY"] = new(States.Any, static (s, c) => s.CapabilityAsync(c)),
        ["NOOP"] = new(States.Any, static (s, c) => s.NoopAsync(c)),
        ["LOGOUT"] = new(States.Any, static (s, c) => s.LogoutAsync(c)),
        ["ID"] = new(States.Any, static (s, c) => s.IdAsync(c)),

        // Not authenticated
        ["STARTTLS"] = new(States.NotAuthenticated, static (s, c) => s.StartTlsAsync(c)),
        ["LOGIN"] = new(States.NotAuthenticated, static (s, c) => s.LoginAsync(c)),
        ["AUTHENTICATE"] = new(States.NotAuthenticated, static (s, c) => s.AuthenticateAsync(c)),

        // Authenticated or selected
        ["ENABLE"] = new(States.Authenticated, static (s, c) => s.EnableAsync(c)),
        ["NAMESPACE"] = new(States.LoggedIn, static (s, c) => s.NamespaceAsync(c)),
        ["SELECT"] = new(States.LoggedIn, static (s, c) => s.SelectAsync(c, examine: false)),
        ["EXAMINE"] = new(States.LoggedIn, static (s, c) => s.SelectAsync(c, examine: true)),
        ["CREATE"] = new(States.LoggedIn, static (s, c) => s.CreateAsync(c)),
        ["DELETE"] = new(States.LoggedIn, static (s, c) => s.DeleteAsync(c)),
        ["RENAME"] = new(States.LoggedIn, static (s, c) => s.RenameAsync(c)),
        ["SUBSCRIBE"] = new(States.LoggedIn, static (s, c) => s.SubscribeAsync(c, subscribe: true)),
        ["UNSUBSCRIBE"] = new(States.LoggedIn, static (s, c) => s.SubscribeAsync(c, subscribe: false)),
        ["LIST"] = new(States.LoggedIn, static (s, c) => s.ListAsync(c)),
        ["LSUB"] = new(States.LoggedIn, static (s, c) => s.LsubAsync(c)),
        ["STATUS"] = new(States.LoggedIn, static (s, c) => s.StatusAsync(c)),
        ["APPEND"] = new(States.LoggedIn, static (s, c) => s.AppendAsync(c)),
        ["IDLE"] = new(States.LoggedIn, static (s, c) => s.IdleAsync(c)),

        // Selected
        ["CHECK"] = new(States.Selected, static (s, c) => s.CheckAsync(c)),
        ["CLOSE"] = new(States.Selected, static (s, c) => s.CloseAsync(c)),
        ["UNSELECT"] = new(States.Selected, static (s, c) => s.UnselectAsync(c)),
        ["EXPUNGE"] = new(States.Selected, static (s, c) => s.ExpungeAsync(c)),
        ["SEARCH"] = new(States.Selected, static (s, c) => s.SearchAsync(c)),
        ["FETCH"] = new(States.Selected, static (s, c) => s.FetchAsync(c)),
        ["STORE"] = new(States.Selected, static (s, c) => s.StoreAsync(c)),
        ["COPY"] = new(States.Selected, static (s, c) => s.CopyAsync(c)),
        ["MOVE"] = new(States.Selected, static (s, c) => s.MoveAsync(c)),
    };
}

/// <summary>The flag columns of one message, as loaded for the snapshot.</summary>
internal sealed record ImapMessageRow(long Id, long Uid, bool IsRead, bool IsAnswered, bool IsStarred, bool IsDeleted, bool IsDraft, bool IsForwarded, string[] Keywords)
{
    public (ImapFlags Flags, string[] Keywords) Flags()
        => (ImapFlagNames.From(IsRead, IsAnswered, IsStarred, IsDeleted, IsDraft, IsForwarded), ImapFlagNames.CleanKeywords(Keywords));

    public ImapMessage ToMessage()
    {
        (ImapFlags flags, string[] keywords) = Flags();
        return new ImapMessage(Id, Uid, flags, keywords);
    }
}

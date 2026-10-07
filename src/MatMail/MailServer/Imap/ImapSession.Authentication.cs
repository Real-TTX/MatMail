using System.Security.Cryptography.X509Certificates;
using System.Text;
using MatMail.Data;
using MatMail.Messaging;

namespace MatMail.MailServer.Imap;

/// <summary>Commands of any state and of the not-authenticated state: capabilities, TLS, sign-in, ID, ENABLE, NAMESPACE.</summary>
internal sealed partial class ImapSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Plain-text sign-in is only offered on encrypted connections when the configuration requires TLS.</summary>
    private bool LoginAllowed => _connection.IsTls || !_context.Config.Imap.RequireTls;

    private bool CanStartTls => !_connection.IsTls && _context.Certificate is not null;

    private string Capabilities()
    {
        var capabilities = new List<string>
        {
            "IMAP4rev1", "LITERAL+", "SASL-IR", "ID", "ENABLE", "IDLE", "NAMESPACE", "UNSELECT", "UIDPLUS", "MOVE",
            "CHILDREN", "SPECIAL-USE", "LIST-EXTENDED", "LIST-STATUS", "APPENDLIMIT=" + ImapRequestReader.MaxAppendSize,
        };

        if (_state == ImapSessionState.NotAuthenticated)
        {
            if (CanStartTls)
            {
                capabilities.Add("STARTTLS");
            }

            capabilities.Add(LoginAllowed ? "AUTH=PLAIN" : "LOGINDISABLED");
        }

        return string.Join(' ', capabilities);
    }

    private Task CapabilityAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        WriteUntagged("CAPABILITY " + Capabilities());
        return CompleteAsync(command, "CAPABILITY completed");
    }

    private Task NoopAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        return CompleteAsync(command, "NOOP completed");
    }

    private Task LogoutAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        WriteUntagged("BYE MatMail IMAP server logging out");
        Tagged(command, "OK", "LOGOUT completed");
        _state = ImapSessionState.Logout;
        return Task.CompletedTask;
    }

    /// <summary>RFC 2971: the client's identification is accepted (and not stored); the server names itself.</summary>
    private Task IdAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        if (!parser.TryReadWord("NIL"))
        {
            parser.Expect('(');
            parser.TrySpace();
            while (!parser.TryConsume(')'))
            {
                parser.ReadString();
                parser.ExpectSpace();
                parser.ReadNString();
                if (parser.Peek() != ')')
                {
                    parser.ExpectSpace();
                }
            }
        }

        parser.ExpectEnd();
        WriteUntagged($"ID (\"name\" \"MatMail\" \"version\" {ImapFormat.String(AppInfo.Version)} \"vendor\" \"MatMail\")");
        return CompleteAsync(command, "ID completed");
    }

    private async Task StartTlsAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        if (_connection.IsTls)
        {
            throw new ImapSyntaxException("TLS is already active");
        }

        X509Certificate2 certificate = _context.Certificate ?? throw new ImapSyntaxException("STARTTLS is not available");
        Tagged(command, "OK", "Begin TLS negotiation now");
        await _connection.FlushAsync(_shutdown);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            await _connection.StartTlsAsync(certificate, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            // Nothing sensible can be said on a connection whose handshake failed.
            _context.Logger.LogDebug(ex, "IMAP STARTTLS with {RemoteIp} failed.", _connection.RemoteIp);
            _state = ImapSessionState.Logout;
        }
    }

    private async Task LoginAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string login = parser.ReadAString();
        parser.ExpectSpace();
        string password = parser.ReadAString();
        parser.ExpectEnd();

        if (!LoginAllowed)
        {
            Tagged(command, "NO", "[PRIVACYREQUIRED] Log in over an encrypted connection (use STARTTLS)");
            return;
        }

        await SignInAsync(command, login, password);
    }

    /// <summary>AUTHENTICATE PLAIN (RFC 4616), with the initial response on the command line (SASL-IR) or after a "+" continuation.</summary>
    private async Task AuthenticateAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string mechanism = parser.ReadAtom();
        string? response = null;
        if (parser.TrySpace() && !parser.AtEnd)
        {
            response = parser.ReadAtom();
        }

        parser.ExpectEnd();

        if (!mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            Tagged(command, "NO", "[CANNOT] Unsupported authentication mechanism");
            return;
        }

        if (!LoginAllowed)
        {
            Tagged(command, "NO", "[PRIVACYREQUIRED] Log in over an encrypted connection (use STARTTLS)");
            return;
        }

        response ??= await ReadSaslResponseAsync();
        if (response is null)
        {
            _state = ImapSessionState.Logout;
            return;
        }

        if (response == "*")
        {
            Tagged(command, "BAD", "Authentication cancelled");
            return;
        }

        string[]? parts = DecodePlain(response);
        if (parts is null)
        {
            Tagged(command, "BAD", "Invalid SASL PLAIN response");
            return;
        }

        (string authorizationId, string login, string password) = (parts[0], parts[1], parts[2]);
        if (authorizationId.Length > 0 && !authorizationId.Equals(login, StringComparison.OrdinalIgnoreCase))
        {
            Tagged(command, "NO", "[AUTHORIZATIONFAILED] Acting as another user is not supported");
            return;
        }

        await SignInAsync(command, login, password);
    }

    /// <summary>The client's response to the empty "+" challenge; null when it disconnects or does not answer in time.</summary>
    private async Task<string?> ReadSaslResponseAsync()
    {
        WriteLine("+ ");
        await _connection.FlushAsync(_shutdown);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        timeout.CancelAfter(Min(_context.IdleTimeout, PreAuthenticationTimeout));
        try
        {
            byte[]? line = await _connection.ReadLineAsync(ImapRequestReader.MaxLineLength, timeout.Token);
            return line is null ? null : Encoding.ASCII.GetString(line).Trim();
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            await SayGoodbyeAsync("BYE Autologout; idle for too long");
            return null;
        }
    }

    /// <summary>authzid NUL authcid NUL passwd; "=" stands for an empty initial response.</summary>
    private static string[]? DecodePlain(string base64)
    {
        try
        {
            byte[] decoded = base64 == "=" ? Array.Empty<byte>() : Convert.FromBase64String(base64);
            string[] parts = Encoding.UTF8.GetString(decoded).Split('\0');
            return parts.Length == 3 ? parts : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks the credentials (MailAccessService: password, active user/tenant, permission mail.use, per-user lockout) and throttles
    /// failures per address: every failure costs a delay, too many failures close the connection, and an address with even more
    /// recent failures is refused without looking at the password.
    /// </summary>
    private async Task SignInAsync(ImapCommand command, string login, string password)
    {
        string remoteIp = _connection.RemoteIp;
        ImapLoginThrottle throttle = _context.Throttle;
        if (throttle.IsBlocked(remoteIp, login))
        {
            Tagged(command, "NO", "[UNAVAILABLE] Too many failed logins from your address; try again later");
            WriteUntagged("BYE Too many failed login attempts");
            _state = ImapSessionState.Logout;
            return;
        }

        TimeSpan penalty = throttle.Penalty(remoteIp);
        if (penalty > TimeSpan.Zero)
        {
            await Task.Delay(penalty, _shutdown);
        }

        MailUser? user;
        await using (ImapWork work = OpenWork())
        {
            user = await work.Access.AuthenticateAsync(login, password, remoteIp);
        }

        if (user is null)
        {
            await RefuseSignInAsync(command, remoteIp, login);
            return;
        }

        _user = user;
        _connectionServices.GetRequiredService<MailAccessService>().Apply(user);
        _state = ImapSessionState.Authenticated;
        ListenForChanges();
        Tagged(command, "OK", $"[CAPABILITY {Capabilities()}] Logged in");
    }

    private async Task RefuseSignInAsync(ImapCommand command, string remoteIp, string login)
    {
        ImapLoginThrottle throttle = _context.Throttle;
        int failures = throttle.RecordFailure(remoteIp, login);
        _failedLogins++;
        await Task.Delay(throttle.FailureDelay, _shutdown);
        Tagged(command, "NO", "[AUTHENTICATIONFAILED] Invalid credentials");

        if (failures >= throttle.DropAfterFailures || _failedLogins >= throttle.DropAfterFailures)
        {
            WriteUntagged("BYE Too many failed login attempts");
            _state = ImapSessionState.Logout;
            await _context.Activity.WarnAsync(ActivityCategory.Imap, $"IMAP: connection from {remoteIp} closed after {failures} failed logins.", remoteIp: remoteIp);
        }
    }

    /// <summary>RFC 5161: accepted, but no extension that needs enabling is supported, so nothing gets enabled.</summary>
    private Task EnableAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        parser.ReadAtom();
        while (parser.TrySpace() && !parser.AtEnd)
        {
            parser.ReadAtom();
        }

        parser.ExpectEnd();
        WriteUntagged("ENABLED");
        return CompleteAsync(command, "ENABLE completed");
    }

    /// <summary>RFC 2342: the own mailbox is the personal namespace, other mailboxes are shared ones below "Shared/".</summary>
    private Task NamespaceAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        WriteUntagged($"NAMESPACE ((\"\" \"/\")) NIL (({ImapFormat.Quote(ImapMailboxTree.SharedPrefix)} \"/\"))");
        return CompleteAsync(command, "NAMESPACE completed");
    }
}

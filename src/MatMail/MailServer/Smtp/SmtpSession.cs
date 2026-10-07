using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MimeKit.Utils;

namespace MatMail.MailServer.Smtp;

/// <summary>What a session needs from the server.</summary>
internal sealed record SmtpSessionContext(
    IServiceScopeFactory Scopes,
    AppConfig Config,
    SmtpAuthThrottle Throttle,
    SmtpActivityLog Activity,
    SmtpServerOptions Options,
    SmtpMemoryBudget Budget,
    ILogger Logger);

/// <summary>
/// One SMTP connection: RFC 5321 with PIPELINING, SIZE, 8BITMIME, ENHANCEDSTATUSCODES, SMTPUTF8, STARTTLS and AUTH PLAIN/LOGIN.
/// Keeps the state of the conversation and asks <see cref="SmtpPolicy"/> what the client may do. The session's scope acts as
/// the system (it looks up domains, addresses and relay rules of every tenant); permissions come from who signed in.
/// </summary>
internal sealed class SmtpSession
{
    /// <summary>RFC 4954 allows AUTH lines of up to 12288 octets; every other command is far shorter.</summary>
    private const int MaxLineLength = 12288;

    private readonly SmtpSessionContext _context;
    private readonly SmtpListenerKind _listener;
    private readonly IPAddress _remote;
    private readonly string _remoteText;
    private readonly X509Certificate2? _certificate;
    private readonly SmtpConnection _connection;
    private readonly SmtpMessageHandler _handler;

    private SmtpPolicy _policy = null!;
    private IReadOnlyList<RelayRule>? _rules;
    private string? _helo;
    private bool _extended;
    private bool _secure;
    private string? _tlsDescription;
    private MailUser? _user;
    private SmtpTransaction? _transaction;
    private int _errors;
    private int _junkCommands;
    private bool _closing;

    public SmtpSession(SmtpSessionContext context, SmtpListenerKind listener, Stream stream, IPAddress remote, X509Certificate2? certificate)
    {
        _context = context;
        _listener = listener;
        _remote = remote;
        _remoteText = remote.ToString();
        _certificate = certificate;
        _connection = new SmtpConnection(stream);
        _handler = new SmtpMessageHandler(context.Scopes);
    }

    private SmtpServerOptions Options => _context.Options;

    private string Hostname => _context.Config.Server.Hostname;

    private long MaxMessageBytes => _context.Config.Smtp.MaxMessageSizeMb > 0 ? _context.Config.Smtp.MaxMessageSizeMb * 1024L * 1024L : 50L * 1024 * 1024;

    private int MaxRecipients => _context.Config.Smtp.MaxRecipients > 0 ? _context.Config.Smtp.MaxRecipients : 100;

    private bool CanStartTls => !_secure && _certificate is not null;

    private bool CanAuthenticate => _secure || !_context.Config.Smtp.RequireTlsForAuth;

    public async Task RunAsync(CancellationToken cancel)
    {
        await using SmtpConnection connection = _connection;
        await using AsyncServiceScope scope = _context.Scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        _policy = new SmtpPolicy(scope.ServiceProvider);

        try
        {
            if (_listener == SmtpListenerKind.ImplicitTls && !await StartTlsAsync(cancel))
            {
                return;
            }

            _connection.Write($"220 {Hostname} ESMTP MatMail");
            while (!_closing)
            {
                SmtpLine line = await _connection.ReadLineAsync(MaxLineLength, Options.CommandTimeout, cancel);
                if (line.Status == SmtpLineStatus.Closed)
                {
                    return;
                }

                if (line.Status == SmtpLineStatus.TooLong)
                {
                    Refuse("500 5.5.2 Error: line too long");
                }
                else
                {
                    await ExecuteAsync(line.Text, cancel);
                }

                if (_errors >= Options.MaxErrors && !_closing)
                {
                    _connection.Write($"421 4.7.0 {Hostname} Error: too many errors");
                    _closing = true;
                }
            }

            await _connection.FlushAsync(Options.CommandTimeout, cancel);
        }
        catch (TimeoutException)
        {
            await SayGoodbyeAsync($"421 4.4.2 {Hostname} Error: timeout exceeded");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await SayGoodbyeAsync($"421 4.3.2 {Hostname} Service shutting down");
        }
    }

    private async Task ExecuteAsync(string line, CancellationToken cancel)
    {
        (string verb, string argument) = SmtpCommandParser.SplitCommand(line);
        try
        {
            switch (verb)
            {
                case "EHLO":
                    Hello(argument, extended: true);
                    break;
                case "HELO":
                    Hello(argument, extended: false);
                    break;
                case "STARTTLS":
                    await StartTlsCommandAsync(argument, cancel);
                    break;
                case "AUTH":
                    await AuthenticateAsync(argument, cancel);
                    break;
                case "MAIL":
                    await MailAsync(argument, cancel);
                    break;
                case "RCPT":
                    await RecipientAsync(argument, cancel);
                    break;
                case "DATA":
                    await DataAsync(argument, cancel);
                    break;
                case "RSET":
                    CountJunkCommand();
                    _transaction = null;
                    _connection.Write("250 2.0.0 OK");
                    break;
                case "NOOP":
                    CountJunkCommand();
                    _connection.Write("250 2.0.0 OK");
                    break;
                case "VRFY":
                    CountJunkCommand();
                    _connection.Write("252 2.5.0 Cannot VRFY user, but will accept message and attempt delivery");
                    break;
                case "HELP":
                    CountJunkCommand();
                    _connection.Write("214 2.0.0 Commands: EHLO HELO STARTTLS AUTH MAIL RCPT DATA RSET NOOP VRFY QUIT");
                    break;
                case "QUIT":
                    _connection.Write($"221 2.0.0 {Hostname} closing connection");
                    _closing = true;
                    break;
                case "EXPN" or "ETRN" or "TURN" or "BDAT":
                    Refuse("502 5.5.1 Error: command not implemented");
                    break;
                case "GET" or "POST" or "HEAD" or "PUT" or "CONNECT" or "OPTIONS":
                    // A web browser or proxy probe on the mail port.
                    _connection.Write($"421 4.7.0 {Hostname} Error: this is an SMTP server");
                    _closing = true;
                    break;
                default:
                    Refuse("500 5.5.2 Error: command not recognized");
                    break;
            }
        }
        catch (Exception ex) when (ex is not (IOException or TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException))
        {
            await ReportInternalErrorAsync(verb, ex);
            _connection.Write("451 4.3.0 Internal server error, please try again later");
            _errors++;
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // EHLO / HELO, STARTTLS
    // -------------------------------------------------------------------------------------------------------------------

    private void Hello(string argument, bool extended)
    {
        if (argument.Length == 0)
        {
            Refuse(extended ? "501 5.5.4 Syntax: EHLO hostname" : "501 5.5.4 Syntax: HELO hostname");
            return;
        }

        // EHLO/HELO starts a new conversation (RFC 5321 4.1.4); a sign-in stays valid.
        _helo = CleanHeloName(argument);
        _extended = extended;
        _transaction = null;
        if (!extended)
        {
            _connection.Write($"250 {Hostname}");
            return;
        }

        var lines = new List<string> { Hostname, "PIPELINING", $"SIZE {MaxMessageBytes}", "8BITMIME", "ENHANCEDSTATUSCODES" };
        if (CanStartTls)
        {
            lines.Add("STARTTLS");
        }

        if (CanAuthenticate)
        {
            lines.Add("AUTH PLAIN LOGIN");
        }

        lines.Add("SMTPUTF8");
        for (int i = 0; i < lines.Count; i++)
        {
            _connection.Write($"250{(i < lines.Count - 1 ? "-" : " ")}{lines[i]}");
        }
    }

    private async Task StartTlsCommandAsync(string argument, CancellationToken cancel)
    {
        if (argument.Length > 0)
        {
            Refuse("501 5.5.4 Syntax: STARTTLS");
            return;
        }

        if (_secure)
        {
            Refuse("503 5.5.1 Error: TLS already active");
            return;
        }

        if (_certificate is null)
        {
            Refuse("454 4.7.0 TLS not available");
            return;
        }

        _connection.Write("220 2.0.0 Ready to start TLS");
        await _connection.FlushAsync(Options.CommandTimeout, cancel);
        if (!await StartTlsAsync(cancel))
        {
            _closing = true;
            return;
        }

        // RFC 3207: everything learnt before TLS is forgotten; the client starts over with EHLO.
        _helo = null;
        _extended = false;
        _transaction = null;
        _user = null;
    }

    /// <summary>The TLS handshake (STARTTLS or port 465). False when it failed; the connection is unusable then.</summary>
    private async Task<bool> StartTlsAsync(CancellationToken cancel)
    {
        if (_certificate is null)
        {
            return false;
        }

        var ssl = new SslStream(_connection.Stream, leaveInnerStreamOpen: false);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(Options.TlsHandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timer.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or Win32Exception)
        {
            _context.Logger.LogDebug(ex, "TLS handshake with {Remote} failed.", _remoteText);
            await ssl.DisposeAsync();
            cancel.ThrowIfCancellationRequested();
            return false;
        }

        _connection.ReplaceStream(ssl);
        _secure = true;
        _tlsDescription = DescribeTls(ssl);
        return true;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // AUTH
    // -------------------------------------------------------------------------------------------------------------------

    private async Task AuthenticateAsync(string argument, CancellationToken cancel)
    {
        if (!_extended)
        {
            Refuse("503 5.5.1 Error: send EHLO first");
            return;
        }

        if (_user is not null)
        {
            Refuse("503 5.5.1 Error: already authenticated");
            return;
        }

        if (_transaction is not null)
        {
            Refuse("503 5.5.1 Error: MAIL transaction in progress");
            return;
        }

        if (!CanAuthenticate)
        {
            Refuse("530 5.7.0 Must issue a STARTTLS command first");
            return;
        }

        if (_context.Throttle.IsBlocked(_remote))
        {
            Refuse("454 4.7.0 Too many failed sign-ins from your address, try again later");
            return;
        }

        string[] parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string mechanism = parts.Length > 0 ? parts[0].ToUpperInvariant() : string.Empty;
        string? initial = parts.Length > 1 ? parts[1] : null;

        (string? login, string? password, string? problem) = mechanism switch
        {
            "PLAIN" => await ReadPlainAsync(initial, cancel),
            "LOGIN" => await ReadLoginAsync(initial, cancel),
            "" => (null, null, "501 5.5.4 Syntax: AUTH mechanism"),
            _ => (null, null, "504 5.5.4 Unrecognized authentication type"),
        };

        if (_closing)
        {
            return;
        }

        if (problem is not null || login is null || password is null)
        {
            Refuse(problem ?? "501 5.5.2 Invalid authentication response");
            return;
        }

        // Reserved right before the check, so parallel connections cannot run more guesses than the client has failures left.
        if (!_context.Throttle.TryBeginAttempt(_remote, login))
        {
            Refuse("454 4.7.0 Too many failed sign-ins from your address, try again later");
            return;
        }

        MailUser? user;
        try
        {
            user = await VerifyAsync(login, password);
        }
        catch
        {
            _context.Throttle.EndAttempt(_remote, login, failed: false);
            throw;
        }

        if (user is null)
        {
            if (_context.Throttle.EndAttempt(_remote, login, failed: true))
            {
                await _context.Activity.WarnAsync(
                    $"auth-blocked:{_remoteText}",
                    $"SMTP: sign-ins from {_remoteText} are blocked for {SmtpAuthThrottle.BlockDuration.TotalMinutes:0} minutes after {SmtpAuthThrottle.MaxFailures} failures.",
                    remoteIp: _remoteText);
            }

            await Task.Delay(Options.AuthFailureDelay, cancel);
            Refuse("535 5.7.8 Authentication credentials invalid");
            return;
        }

        _context.Throttle.EndAttempt(_remote, login, failed: false);
        _user = user;
        _connection.Write("235 2.7.0 Authentication successful");
    }

    /// <summary>AUTH PLAIN (RFC 4616): base64 of "authzid NUL login NUL password".</summary>
    private async Task<(string? Login, string? Password, string? Problem)> ReadPlainAsync(string? initial, CancellationToken cancel)
    {
        string? response = initial ?? await ReadResponseAsync("334 ", cancel);
        if (response is null)
        {
            return (null, null, null);
        }

        if (response == "*")
        {
            return (null, null, "501 5.7.0 Authentication cancelled");
        }

        if (!TryDecodeBase64(response == "=" ? string.Empty : response, out string decoded))
        {
            return (null, null, "501 5.5.2 Cannot decode the response");
        }

        string[] fields = decoded.Split('\0');
        if (fields.Length != 3)
        {
            return (null, null, "501 5.5.2 Invalid PLAIN response");
        }

        // Acting on behalf of somebody else (an authorization identity) is not supported.
        if (fields[0].Length > 0 && !string.Equals(fields[0], fields[1], StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, "535 5.7.8 Authentication credentials invalid");
        }

        return (fields[1], fields[2], null);
    }

    /// <summary>AUTH LOGIN: user name and password, each asked for and sent in base64.</summary>
    private async Task<(string? Login, string? Password, string? Problem)> ReadLoginAsync(string? initial, CancellationToken cancel)
    {
        string? loginResponse = initial ?? await ReadResponseAsync("334 VXNlcm5hbWU6", cancel);
        if (loginResponse is null)
        {
            return (null, null, null);
        }

        if (loginResponse == "*")
        {
            return (null, null, "501 5.7.0 Authentication cancelled");
        }

        if (!TryDecodeBase64(loginResponse, out string login))
        {
            return (null, null, "501 5.5.2 Cannot decode the response");
        }

        string? passwordResponse = await ReadResponseAsync("334 UGFzc3dvcmQ6", cancel);
        if (passwordResponse is null)
        {
            return (null, null, null);
        }

        if (passwordResponse == "*")
        {
            return (null, null, "501 5.7.0 Authentication cancelled");
        }

        return TryDecodeBase64(passwordResponse, out string password)
            ? (login, password, null)
            : (null, null, "501 5.5.2 Cannot decode the response");
    }

    /// <summary>Sends a challenge and reads the client's answer. Null when the connection is gone.</summary>
    private async Task<string?> ReadResponseAsync(string challenge, CancellationToken cancel)
    {
        _connection.Write(challenge);
        SmtpLine line = await _connection.ReadLineAsync(MaxLineLength, Options.CommandTimeout, cancel);
        if (line.Status == SmtpLineStatus.Closed)
        {
            _closing = true;
            return null;
        }

        // A line that is too long is not valid base64 either.
        return line.Status == SmtpLineStatus.TooLong ? "!" : line.Text.Trim();
    }

    /// <summary>
    /// Checks the credentials in a scope of its own: a successful check makes that scope act as the user, while the session stays
    /// the system. Passwords are never logged.
    /// </summary>
    private async Task<MailUser?> VerifyAsync(string login, string password)
    {
        await using AsyncServiceScope scope = _context.Scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        return await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync(login, password, _remoteText);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // MAIL, RCPT, DATA
    // -------------------------------------------------------------------------------------------------------------------

    private async Task MailAsync(string argument, CancellationToken cancel)
    {
        if (_helo is null)
        {
            Refuse("503 5.5.1 Error: send HELO/EHLO first");
            return;
        }

        if (_transaction is not null)
        {
            Refuse("503 5.5.1 Error: nested MAIL command");
            return;
        }

        SmtpPath? path = SmtpCommandParser.ParsePath(argument, "FROM:");
        if (path is null)
        {
            Refuse("501 5.5.4 Syntax: MAIL FROM:<address>");
            return;
        }

        string? parameterProblem = CheckMailParameters(path.Parameters);
        if (parameterProblem is not null)
        {
            Refuse(parameterProblem);
            return;
        }

        string sender = MailAddresses.Normalize(path.Address);
        if (sender.Length > 0 && (!MailAddresses.IsValid(sender) || MailAddresses.IsCatchAll(sender)))
        {
            Refuse("501 5.1.7 Bad sender address syntax");
            return;
        }

        if (_user is not null && !await _policy.IsStillActiveAsync(_user, cancel))
        {
            // Disabled while this session was open: the sign-in does not count any more.
            _user = null;
            Refuse("530 5.7.0 Authentication required");
            return;
        }

        if (_user is null)
        {
            _rules ??= await _policy.FindRulesAsync(_remote, cancel);
        }

        bool submissionPort = _listener != SmtpListenerKind.Relay;
        (SmtpTransaction? transaction, string? rejection) = await _policy.CheckSenderAsync(
            sender, _user, _user is null ? _rules! : Array.Empty<RelayRule>(), submissionPort, cancel);
        if (transaction is null)
        {
            await ReportRefusedSenderAsync(sender, rejection!);
            Refuse(rejection!);
            return;
        }

        _transaction = transaction;
        _connection.Write("250 2.1.0 Sender OK");
    }

    private string? CheckMailParameters(IReadOnlyDictionary<string, string?> parameters)
    {
        if (!_extended && parameters.Count > 0)
        {
            return "555 5.5.4 Parameters need EHLO";
        }

        foreach ((string key, string? value) in parameters)
        {
            switch (key)
            {
                case "SIZE":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                    {
                        return "501 5.5.4 Bad SIZE parameter";
                    }

                    if (size > MaxMessageBytes)
                    {
                        return "552 5.3.4 Message size exceeds fixed maximum message size";
                    }

                    break;
                case "BODY":
                    if (!string.Equals(value, "7BIT", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "8BITMIME", StringComparison.OrdinalIgnoreCase))
                    {
                        return "555 5.5.4 Unsupported BODY type";
                    }

                    break;
                case "SMTPUTF8":
                    if (value is not null)
                    {
                        return "555 5.5.4 SMTPUTF8 takes no value";
                    }

                    break;
                case "AUTH":
                    // RFC 4954: the AUTH= parameter of MAIL is accepted, but never trusted.
                    break;
                default:
                    return $"555 5.5.4 Unsupported parameter {(key.Length > 40 ? key[..40] : key)}";
            }
        }

        return null;
    }

    private async Task RecipientAsync(string argument, CancellationToken cancel)
    {
        if (_transaction is null)
        {
            Refuse("503 5.5.1 Error: need MAIL command");
            return;
        }

        SmtpPath? path = SmtpCommandParser.ParsePath(argument, "TO:");
        if (path is null)
        {
            Refuse("501 5.5.4 Syntax: RCPT TO:<address>");
            return;
        }

        if (path.Parameters.Count > 0)
        {
            Refuse("555 5.5.4 Unsupported parameters");
            return;
        }

        string recipient = MailAddresses.Normalize(path.Address);
        if (recipient == "postmaster")
        {
            // RFC 5321 4.5.1: "<Postmaster>" without a domain must be accepted.
            recipient = await _policy.PostmasterAddressAsync(Hostname, cancel);
        }

        if (!MailAddresses.IsValid(recipient) || MailAddresses.IsCatchAll(recipient))
        {
            Refuse("501 5.1.3 Bad recipient address syntax");
            return;
        }

        if (_transaction.Recipients.Contains(recipient))
        {
            _connection.Write("250 2.1.5 Recipient OK");
            return;
        }

        if (_transaction.Recipients.Count >= MaxRecipients)
        {
            // Not counted as an error: the client sends the rest in another transaction (RFC 5321 4.5.3.1.10).
            _connection.Write("452 4.5.3 Error: too many recipients");
            return;
        }

        string? rejection = await _policy.CheckRecipientAsync(_transaction, recipient, cancel);
        if (rejection is not null)
        {
            if (_transaction.Kind == SmtpClientKind.Anonymous)
            {
                await _context.Activity.WarnAsync(
                    $"relay-denied:{_remoteText}",
                    $"SMTP: relay access denied for {_remoteText} ({Show(_transaction.Sender)} to {recipient}).",
                    remoteIp: _remoteText);
            }

            Refuse(rejection);
            return;
        }

        _transaction.Recipients.Add(recipient);
        _connection.Write("250 2.1.5 Recipient OK");
    }

    private async Task DataAsync(string argument, CancellationToken cancel)
    {
        if (argument.Length > 0)
        {
            Refuse("501 5.5.4 Syntax: DATA");
            return;
        }

        if (_transaction is null)
        {
            Refuse("503 5.5.1 Error: need MAIL command");
            return;
        }

        if (_transaction.Recipients.Count == 0)
        {
            Refuse("503 5.5.1 Error: need RCPT command");
            return;
        }

        // Whatever happens from here on, the transaction ends with this DATA.
        SmtpTransaction transaction = _transaction;
        _transaction = null;

        _connection.Write("354 End data with <CR><LF>.<CR><LF>");
        using var buffer = new MemoryStream();
        try
        {
            SmtpDataStatus status = await _connection.ReadDataAsync(
                buffer, MaxMessageBytes, Options.DataTimeout, cancel, _context.Budget, DateTime.UtcNow + Options.MaxDataDuration);
            switch (status)
            {
                case SmtpDataStatus.Closed:
                    _closing = true;
                    return;
                case SmtpDataStatus.TooBig:
                    _connection.Write("552 5.3.4 Message size exceeds fixed maximum message size");
                    return;
                case SmtpDataStatus.NoMemory:
                    await _context.Activity.WarnAsync(
                        "memory-budget",
                        $"SMTP: a message from {_remoteText} was refused for now: too many large messages are being received at the same time.",
                        remoteIp: _remoteText);
                    _connection.Write("452 4.3.1 Insufficient system storage, please try again later");
                    return;
            }

            if (buffer.Length == 0)
            {
                Refuse("554 5.6.0 Error: the message is empty");
                return;
            }

            _connection.Write(await AcceptMessageAsync(transaction, buffer.ToArray(), cancel));
        }
        finally
        {
            // The message's share of the server-wide budget is free again once it is delivered (or refused).
            _context.Budget.Release(buffer.Length);
        }
    }

    /// <summary>Checks the From: header of signed-in users, stamps the trace headers and delivers or submits the message.</summary>
    private async Task<string> AcceptMessageAsync(SmtpTransaction transaction, byte[] raw, CancellationToken cancel)
    {
        string queueId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        try
        {
            if (SmtpMessageHandler.CountHeader(raw, "Received") >= SmtpMessageHandler.MaxHops)
            {
                await _context.Activity.WarnAsync(
                    $"loop:{_remoteText}",
                    $"SMTP: refused a message from {_remoteText} with {SmtpMessageHandler.MaxHops} or more Received headers (mail loop).",
                    remoteIp: _remoteText);
                return "554 5.4.6 Too many hops, the message is looping";
            }

            long? mailboxId = transaction.Identity?.Mailbox.Id;
            if (transaction.User is MailUser user)
            {
                (string? rejection, SendIdentity? fromIdentity) = await _policy.CheckHeaderSendersAsync(user, raw, cancel);
                if (rejection is not null)
                {
                    await _context.Activity.WarnAsync(
                        $"from-not-owned:{user.UserId}:{_remoteText}",
                        $"SMTP: '{user.LoginName}' sent a message with a From: address they may not use ({_remoteText}).",
                        user.TenantId,
                        user.UserId,
                        _remoteText);
                    _errors++;
                    return rejection;
                }

                // Footers and the like follow the mailbox the recipients see as sender.
                mailboxId = fromIdentity?.Mailbox.Id ?? mailboxId;
            }

            byte[] stamped = SmtpMessageHandler.AddTraceHeaders(raw, transaction.Sender, BuildReceivedHeader(transaction, queueId));
            string reply = await _handler.HandleAsync(transaction, stamped, mailboxId, queueId, cancel);
            if (transaction.Rule is RelayRule rule && reply.StartsWith("250", StringComparison.Ordinal))
            {
                await _context.Activity.InfoAsync(
                    $"relay-rule:{rule.Id}:{_remoteText}",
                    $"SMTP: relay rule '{rule.Name}' used by {_remoteText} (sender {Show(transaction.Sender)}).",
                    rule.TenantId,
                    remoteIp: _remoteText);
            }

            return reply;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or TimeoutException))
        {
            await ReportInternalErrorAsync("DATA", ex);
            return "451 4.3.0 Temporary server error, please try again later";
        }
    }

    /// <summary>"Received: from helo ([ip]) (using TLS…) by host (MatMail) with ESMTPSA id X for &lt;rcpt&gt;; date" (RFC 5321 4.4).</summary>
    private string BuildReceivedHeader(SmtpTransaction transaction, string queueId)
    {
        string protocol = !_extended ? "SMTP" : "ESMTP" + (_secure ? "S" : string.Empty) + (_user is not null ? "A" : string.Empty);
        string address = _remote.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6:" + _remoteText : _remoteText;

        var header = new StringBuilder();
        header.Append("Received: from ").Append(_helo ?? "unknown").Append(" ([").Append(address).Append("])");
        if (_tlsDescription is not null)
        {
            header.Append("\r\n\t(using ").Append(_tlsDescription).Append(')');
        }

        header.Append("\r\n\tby ").Append(Hostname).Append(" (MatMail) with ").Append(protocol).Append(" id ").Append(queueId);
        if (transaction.Recipients.Count == 1)
        {
            // Only for a single recipient: listing several would reveal Bcc recipients to each other.
            header.Append("\r\n\tfor <").Append(transaction.Recipients[0]).Append('>');
        }

        header.Append(";\r\n\t").Append(DateUtils.FormatDate(DateTimeOffset.UtcNow));
        return header.ToString();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>A reply that refuses what the client asked for; counts towards the error limit of the session.</summary>
    private void Refuse(string reply)
    {
        _connection.Write(reply);
        _errors++;
    }

    /// <summary>Commands that do nothing are fine, but not endlessly: beyond the limit each one counts as an error.</summary>
    private void CountJunkCommand()
    {
        if (++_junkCommands > Options.MaxJunkCommands)
        {
            _errors++;
        }
    }

    private async Task SayGoodbyeAsync(string reply)
    {
        try
        {
            _connection.Write(reply);
            await _connection.FlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception)
        {
            // The client is gone already.
        }
    }

    private Task ReportRefusedSenderAsync(string sender, string rejection)
    {
        if (_user is not null)
        {
            return _context.Activity.WarnAsync(
                $"sender-not-owned:{_user.UserId}:{_remoteText}",
                $"SMTP: '{_user.LoginName}' may not send as {Show(sender)} ({_remoteText}).",
                _user.TenantId,
                _user.UserId,
                _remoteText);
        }

        if (rejection.StartsWith("550", StringComparison.Ordinal))
        {
            return _context.Activity.WarnAsync(
                $"local-sender:{_remoteText}",
                $"SMTP: refused mail from {_remoteText} that claims the local sender {sender} without signing in.",
                remoteIp: _remoteText);
        }

        return Task.CompletedTask;
    }

    private async Task ReportInternalErrorAsync(string command, Exception ex)
    {
        _context.Logger.LogError(ex, "SMTP {Command} from {Remote} failed.", command, _remoteText);
        await _context.Activity.ErrorAsync(
            $"error:{command}:{ex.GetType().Name}",
            $"SMTP {command} from {_remoteText} failed: {ex.Message}",
            remoteIp: _remoteText,
            details: ex.ToString());
    }

    private static string Show(string sender) => sender.Length == 0 ? "<>" : sender;

    /// <summary>The EHLO name as it may appear in the Received header: one token of host name characters, no control characters.</summary>
    private static string CleanHeloName(string argument)
    {
        string name = argument.Split(' ', 2)[0];
        if (name.Length > 255)
        {
            name = name[..255];
        }

        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']' ? c : '?');
        }

        return text.ToString();
    }

    private static bool TryDecodeBase64(string text, out string value)
    {
        value = string.Empty;
        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DescribeTls(SslStream ssl)
    {
        string protocol = ssl.SslProtocol switch
        {
            SslProtocols.Tls13 => "TLSv1.3",
            SslProtocols.Tls12 => "TLSv1.2",
            SslProtocols other => other.ToString(),
        };

        try
        {
            return $"{protocol} with cipher {ssl.NegotiatedCipherSuite}";
        }
        catch (Exception)
        {
            return protocol;
        }
    }
}

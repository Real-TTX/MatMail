using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MatMail.Configuration;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Where the IMAP server listens. Without it the configuration decides (<see cref="ImapConfig"/>: port 0 = off). Here a null port
/// is off and port 0 picks any free port (tests read the bound ports from <see cref="ImapServer.PlainEndpoint"/> and
/// <see cref="ImapServer.TlsEndpoint"/>).
/// </summary>
public sealed record ImapListenOptions(IPAddress Address, int? PlainPort, int? TlsPort);

/// <summary>What all sessions of one server share.</summary>
internal sealed class ImapServerContext
{
    public required IServiceScopeFactory Scopes { get; init; }

    public required AppConfig Config { get; init; }

    public required MailEventHub Hub { get; init; }

    public required ImapLoginThrottle Throttle { get; init; }

    public required ActivityLogger Activity { get; init; }

    public required ILogger Logger { get; init; }

    public CertificateProvider? Certificates { get; init; }

    /// <summary>The certificate to present right now (renewed certificates are picked up for new handshakes).</summary>
    public X509Certificate2? Certificate => Certificates?.Current;

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often an open session looks up whether its user and the selected mailbox are still allowed.</summary>
    public TimeSpan AccessRecheckInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The IMAP4rev1 server (RFC 3501 with IDLE, UIDPLUS, MOVE, LITERAL+, SASL-IR, SPECIAL-USE, LIST-EXTENDED, LIST-STATUS, ...):
/// a plain listener with STARTTLS (port 143) and an implicit-TLS listener (port 993). Every connection gets its own DI scope; a
/// failing connection never affects the listener or other sessions. On shutdown every session says BYE.
/// </summary>
public sealed class ImapServer : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly ImapServerContext _context;
    private readonly ImapListenOptions? _listen;
    private readonly ILogger<ImapServer> _logger;
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImapServer(
        IServiceScopeFactory scopes,
        AppConfig config,
        MailEventHub hub,
        ImapLoginThrottle throttle,
        ActivityLogger activity,
        ILogger<ImapServer> logger,
        CertificateProvider? certificates = null,
        ImapListenOptions? listen = null)
    {
        _logger = logger;
        _listen = listen;
        _context = new ImapServerContext
        {
            Scopes = scopes,
            Config = config,
            Hub = hub,
            Throttle = throttle,
            Activity = activity,
            Logger = logger,
            Certificates = certificates,
        };
    }

    /// <summary>The bound endpoint of the plain listener (STARTTLS); null when it is off or not started yet.</summary>
    public IPEndPoint? PlainEndpoint { get; private set; }

    /// <summary>The bound endpoint of the implicit-TLS listener; null when it is off, there is no certificate, or not started yet.</summary>
    public IPEndPoint? TlsEndpoint { get; private set; }

    /// <summary>Completes when the listeners are bound (or IMAP is switched off); fails when a port cannot be bound.</summary>
    public Task Started => _started.Task;

    /// <summary>Open client connections.</summary>
    public int ConnectionCount => _sessions.Count;

    /// <summary>Sessions without any command for this long are logged out (RFC 3501: at least 30 minutes).</summary>
    public TimeSpan IdleTimeout
    {
        get => _context.IdleTimeout;
        set => _context.IdleTimeout = value;
    }

    /// <summary>How often an idling session checks its folder even without a change notification.</summary>
    public TimeSpan IdlePollInterval
    {
        get => _context.IdlePollInterval;
        set => _context.IdlePollInterval = value;
    }

    /// <summary>How often an open session looks up whether its user and the selected mailbox are still allowed (tests: zero).</summary>
    public TimeSpan AccessRecheckInterval
    {
        get => _context.AccessRecheckInterval;
        set => _context.AccessRecheckInterval = value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_context.Config.Imap.Enabled)
        {
            _logger.LogInformation("The IMAP server is switched off.");
            _started.TrySetResult();
            return;
        }

        List<(TcpListener Listener, bool ImplicitTls)> listeners;
        try
        {
            listeners = StartListeners();
            _started.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The IMAP server cannot listen on the configured ports; IMAP is not available.");
            _started.TrySetException(ex);
            return;
        }

        try
        {
            await Task.WhenAll(listeners.Select(l => AcceptLoopAsync(l.Listener, l.ImplicitTls, stoppingToken)));
        }
        catch (Exception ex)
        {
            // Never take the whole application (web interface, SMTP) down with the IMAP server.
            _logger.LogError(ex, "The IMAP server stopped unexpectedly.");
        }
        finally
        {
            listeners.ForEach(l => l.Listener.Stop());
            await WaitForSessionsAsync();
        }
    }

    private List<(TcpListener Listener, bool ImplicitTls)> StartListeners()
    {
        ImapConfig imap = _context.Config.Imap;
        ImapListenOptions listen = _listen ?? new ImapListenOptions(
            ParseAddress(imap.BindAddress),
            imap.Port > 0 ? imap.Port : null,
            imap.ImplicitTlsPort > 0 ? imap.ImplicitTlsPort : null);

        var listeners = new List<(TcpListener, bool)>();
        try
        {
            if (listen.PlainPort is int plainPort)
            {
                TcpListener plain = Listen(listen.Address, plainPort);
                listeners.Add((plain, false));
                PlainEndpoint = (IPEndPoint)plain.LocalEndpoint;
            }

            if (listen.TlsPort is int tlsPort && _context.Certificate is null)
            {
                _logger.LogWarning("There is no TLS certificate: the IMAP TLS port {Port} is not started and STARTTLS is not offered.", tlsPort);
            }
            else if (listen.TlsPort is int port)
            {
                TcpListener tls = Listen(listen.Address, port);
                listeners.Add((tls, true));
                TlsEndpoint = (IPEndPoint)tls.LocalEndpoint;
            }
        }
        catch
        {
            listeners.ForEach(l => l.Item1.Stop());
            throw;
        }

        if (imap.RequireTls && _context.Certificate is null)
        {
            _logger.LogWarning("IMAP requires TLS for sign-in, but there is no certificate: nobody can sign in over IMAP.");
        }

        _logger.LogInformation("IMAP server listening: plain/STARTTLS {Plain}, TLS {Tls}.", PlainEndpoint?.ToString() ?? "off", TlsEndpoint?.ToString() ?? "off");
        return listeners;
    }

    private static TcpListener Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any))
        {
            listener.Server.DualMode = true;
        }

        listener.Start(128);
        return listener;
    }

    private static IPAddress ParseAddress(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress.Trim() is "*" or "+")
        {
            return IPAddress.Any;
        }

        return IPAddress.TryParse(bindAddress.Trim(), out IPAddress? address) ? address : IPAddress.Any;
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool implicitTls, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(stopping);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // E.g. out of file handles: try again after a short pause instead of spinning.
                _logger.LogDebug(ex, "Accepting an IMAP connection failed.");
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                continue;
            }

            Track(HandleConnectionAsync(socket, implicitTls, stopping));
        }
    }

    private void Track(Task session)
    {
        _sessions.TryAdd(session, 0);
        session.ContinueWith(t => _sessions.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(Socket socket, bool implicitTls, CancellationToken stopping)
    {
        await Task.Yield();
        string remoteIp = RemoteAddress(socket);
        if (!TryEnter(remoteIp))
        {
            // On the TLS port the client expects a handshake, not text: the connection is just closed there.
            await RefuseAsync(socket, implicitTls ? null : "* BYE Too many connections from your address\r\n");
            return;
        }

        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (implicitTls)
            {
                stream = await HandshakeAsync(stream, stopping);
            }

            await using var connection = new ImapConnection(stream, remoteIp, implicitTls);
            await using AsyncServiceScope scope = _context.Scopes.CreateAsyncScope();
            var session = new ImapSession(connection, _context, scope.ServiceProvider, stopping);
            await session.RunAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            _logger.LogDebug(ex, "IMAP connection from {RemoteIp} ended.", remoteIp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IMAP connection from {RemoteIp} failed.", remoteIp);
        }
        finally
        {
            Leave(remoteIp);
            socket.Dispose();
        }
    }

    private async Task<Stream> HandshakeAsync(Stream stream, CancellationToken stopping)
    {
        X509Certificate2? certificate = _context.Certificate;
        if (certificate is null)
        {
            await stream.DisposeAsync();
            throw new IOException("No TLS certificate available.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(HandshakeTimeout);
        return await ImapConnection.AuthenticateAsync(stream, certificate, timeout.Token);
    }

    private bool TryEnter(string remoteIp)
    {
        int limit = _context.Config.Imap.MaxConnectionsPerIp;
        int count = _connectionsPerIp.AddOrUpdate(remoteIp, 1, (_, current) => current + 1);
        if (limit <= 0 || count <= limit)
        {
            return true;
        }

        Leave(remoteIp);
        _logger.LogWarning("IMAP connection from {RemoteIp} refused: more than {Limit} connections.", remoteIp, limit);
        return false;
    }

    private void Leave(string remoteIp)
    {
        int remaining = _connectionsPerIp.AddOrUpdate(remoteIp, 0, (_, current) => Math.Max(0, current - 1));
        if (remaining == 0)
        {
            _connectionsPerIp.TryRemove(new KeyValuePair<string, int>(remoteIp, 0));
        }
    }

    private static async Task RefuseAsync(Socket socket, string? message)
    {
        try
        {
            if (message is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.SendAsync(Encoding.ASCII.GetBytes(message), SocketFlags.None, timeout.Token);
            }

            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client is gone already.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static string RemoteAddress(Socket socket)
    {
        IPAddress? address = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        if (address is null)
        {
            return "unknown";
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private async Task WaitForSessionsAsync()
    {
        Task[] running = _sessions.Keys.ToArray();
        if (running.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(ShutdownGrace);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Count} IMAP sessions did not end in time.", running.Count(t => !t.IsCompleted));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "An IMAP session ended with an error during shutdown.");
        }
    }
}

public static class ImapServerRegistration
{
    /// <summary>The IMAP server (hosted service) and its login throttle.</summary>
    public static IServiceCollection AddImapServer(this IServiceCollection services)
    {
        services.TryAddSingleton<ImapLoginThrottle>();
        services.TryAddSingleton<ImapServer>();
        services.AddHostedService(provider => provider.GetRequiredService<ImapServer>());
        return services;
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MatMail.Configuration;
using MatMail.Services;

namespace MatMail.MailServer.Smtp;

/// <summary>The kinds of SMTP listeners.</summary>
public enum SmtpListenerKind
{
    /// <summary>Port 25: mail from other servers and the smart host for trusted networks; STARTTLS is offered.</summary>
    Relay,

    /// <summary>Port 587: mail clients; STARTTLS, sign-in only after TLS.</summary>
    Submission,

    /// <summary>Port 465: mail clients with TLS from the first byte.</summary>
    ImplicitTls,
}

/// <summary>One address and port to listen on. Port 0 takes any free port (tests); <see cref="SmtpServer.BoundPorts"/> tells which.</summary>
public sealed record SmtpEndpoint(SmtpListenerKind Kind, IPAddress Address, int Port)
{
    /// <summary>The listeners of the configuration: every port above 0 on the bind address.</summary>
    public static IReadOnlyList<SmtpEndpoint> FromConfig(SmtpConfig config)
    {
        IPAddress address = ParseBindAddress(config.BindAddress) ?? IPAddress.Any;
        var endpoints = new List<SmtpEndpoint>();
        if (config.Port > 0)
        {
            endpoints.Add(new SmtpEndpoint(SmtpListenerKind.Relay, address, config.Port));
        }

        if (config.SubmissionPort > 0)
        {
            endpoints.Add(new SmtpEndpoint(SmtpListenerKind.Submission, address, config.SubmissionPort));
        }

        if (config.ImplicitTlsPort > 0)
        {
            endpoints.Add(new SmtpEndpoint(SmtpListenerKind.ImplicitTls, address, config.ImplicitTlsPort));
        }

        return endpoints;
    }

    /// <summary>"0.0.0.0", "::" (IPv4 and IPv6), a single address, or empty/"*" for every IPv4 address. Null when unreadable.</summary>
    public static IPAddress? ParseBindAddress(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value == "*")
        {
            return IPAddress.Any;
        }

        return IPAddress.TryParse(value, out IPAddress? address) ? address : null;
    }
}

/// <summary>Limits and timeouts of the SMTP sessions. The defaults follow RFC 5321; tests shorten them.</summary>
internal sealed class SmtpServerOptions
{
    /// <summary>The listeners; null = from the configuration.</summary>
    public IReadOnlyList<SmtpEndpoint>? Endpoints { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Every failed sign-in costs the client this long (slows down password guessing).</summary>
    public TimeSpan AuthFailureDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Refused commands after which the connection is closed.</summary>
    public int MaxErrors { get; init; } = 10;

    /// <summary>Connections at the same time, over all addresses.</summary>
    public int MaxConnections { get; init; } = 500;
}

/// <summary>
/// The SMTP server: up to three listeners (25 relay/MX, 587 submission, 465 implicit TLS) from <see cref="AppConfig.Smtp"/>, each
/// connection in its own <see cref="SmtpSession"/> with its own DI scope. A failing port or connection never stops the others.
/// TLS uses the certificate of <see cref="CertificateProvider"/>; without one there is neither STARTTLS nor port 465.
/// </summary>
public sealed class SmtpServer : BackgroundService
{
    private const int DefaultConnectionsPerAddress = 30;
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly CertificateProvider? _certificates;
    private readonly SmtpAuthThrottle _throttle;
    private readonly SmtpActivityLog _activity;
    private readonly ILogger<SmtpServer> _logger;
    private readonly SmtpServerOptions _options;

    private readonly List<(SmtpListenerKind Kind, TcpListener Listener)> _listeners = new();
    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerAddress = new();
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private long _nextSessionId;
    private int _connections;

    public SmtpServer(
        IServiceScopeFactory scopes, AppConfig config, SmtpAuthThrottle throttle, SmtpActivityLog activity, ILogger<SmtpServer> logger,
        CertificateProvider? certificates = null)
        : this(scopes, config, throttle, activity, logger, certificates, new SmtpServerOptions())
    {
    }

    internal SmtpServer(
        IServiceScopeFactory scopes, AppConfig config, SmtpAuthThrottle throttle, SmtpActivityLog activity, ILogger<SmtpServer> logger,
        CertificateProvider? certificates, SmtpServerOptions options)
    {
        _scopes = scopes;
        _config = config;
        _throttle = throttle;
        _activity = activity;
        _logger = logger;
        _certificates = certificates;
        _options = options;
    }

    /// <summary>The ports actually listened on, by listener kind (filled when the server starts).</summary>
    public IReadOnlyDictionary<SmtpListenerKind, int> BoundPorts { get; private set; } = new Dictionary<SmtpListenerKind, int>();

    /// <summary>Connections that are open right now.</summary>
    public int ActiveConnections => Volatile.Read(ref _connections);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The ports are opened here, not in the background, so they are known (and failures logged) once start-up is done.
        if (_config.Smtp.Enabled)
        {
            OpenListeners();
        }
        else
        {
            _logger.LogInformation("The SMTP server is switched off.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_listeners.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(_listeners.Select(l => AcceptLoopAsync(l.Kind, l.Listener, stoppingToken)));
        }
        finally
        {
            foreach ((_, TcpListener listener) in _listeners)
            {
                listener.Stop();
            }

            await WaitForSessionsAsync();
        }
    }

    private void OpenListeners()
    {
        if (SmtpEndpoint.ParseBindAddress(_config.Smtp.BindAddress) is null)
        {
            _logger.LogWarning("SMTP bind address '{Address}' is not an IP address; listening on all IPv4 addresses.", _config.Smtp.BindAddress);
        }

        var bound = new Dictionary<SmtpListenerKind, int>();
        foreach (SmtpEndpoint endpoint in _options.Endpoints ?? SmtpEndpoint.FromConfig(_config.Smtp))
        {
            if (endpoint.Kind == SmtpListenerKind.ImplicitTls && _certificates?.Current is null)
            {
                _logger.LogWarning("SMTP port {Port} (TLS) is not opened: there is no TLS certificate.", endpoint.Port);
                continue;
            }

            try
            {
                var listener = new TcpListener(endpoint.Address, endpoint.Port);
                if (endpoint.Address.Equals(IPAddress.IPv6Any))
                {
                    listener.Server.DualMode = true;
                }

                listener.Start(backlog: 128);
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listeners.Add((endpoint.Kind, listener));
                bound[endpoint.Kind] = port;
                _logger.LogInformation("SMTP listening on {Address}:{Port} ({Kind}).", endpoint.Address, port, endpoint.Kind);
            }
            catch (Exception ex)
            {
                // A port in use or a bad port number must not stop the rest of the application (the web interface).
                _logger.LogError(ex, "SMTP port {Port} could not be opened.", endpoint.Port);
                _ = _activity.ErrorAsync($"listen:{endpoint.Port}", $"SMTP port {endpoint.Port} ({endpoint.Kind}) could not be opened: {ex.Message}");
            }
        }

        BoundPorts = bound;
    }

    private async Task AcceptLoopAsync(SmtpListenerKind kind, TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (!stopping.IsCancellationRequested)
            {
                // E.g. a client that reset the connection before it was accepted; the listener itself is fine.
                _logger.LogDebug(ex, "Accepting an SMTP connection failed.");
                continue;
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                // Never let the listener die (a failing hosted service would stop the whole application).
                _logger.LogError(ex, "Accepting an SMTP connection failed.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                continue;
            }
            catch (Exception)
            {
                break;
            }

            long id = Interlocked.Increment(ref _nextSessionId);
            Task session = HandleClientAsync(kind, client, stopping);
            _sessions[id] = session;
            _ = session.ContinueWith(_ => _sessions.TryRemove(id, out Task? _), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(SmtpListenerKind kind, TcpClient client, CancellationToken stopping)
    {
        // Let the accept loop go on at once.
        await Task.Yield();

        using (client)
        {
            IPAddress remote;
            try
            {
                remote = Unmap(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Gone before it could be looked at.
                return;
            }

            string? refusal = Admit(remote);
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                if (refusal is not null)
                {
                    await RefuseAsync(kind, stream, refusal);
                    return;
                }

                var context = new SmtpSessionContext(_scopes, _config, _throttle, _activity, _options, _logger);
                var session = new SmtpSession(context, kind, stream, remote, _certificates?.Current);
                await session.RunAsync(stopping);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "SMTP connection from {Remote} ended.", remote);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMTP connection from {Remote} failed.", remote);
            }
            finally
            {
                Release(remote);
            }
        }
    }

    /// <summary>Counts the connection; returns the refusing reply when there are too many (the connection is counted anyway).</summary>
    private string? Admit(IPAddress remote)
    {
        int total = Interlocked.Increment(ref _connections);
        int fromAddress = _connectionsPerAddress.AddOrUpdate(remote, 1, (_, count) => count + 1);
        int perAddressLimit = _config.Smtp.MaxConnectionsPerIp > 0 ? _config.Smtp.MaxConnectionsPerIp : DefaultConnectionsPerAddress;

        if (total > _options.MaxConnections)
        {
            return $"421 4.7.0 {_config.Server.Hostname} Too many connections, try again later";
        }

        return fromAddress > perAddressLimit
            ? $"421 4.7.0 {_config.Server.Hostname} Too many connections from your address, try again later"
            : null;
    }

    private void Release(IPAddress remote)
    {
        Interlocked.Decrement(ref _connections);
        while (_connectionsPerAddress.TryGetValue(remote, out int count))
        {
            bool done = count <= 1
                ? _connectionsPerAddress.TryRemove(new KeyValuePair<IPAddress, int>(remote, count))
                : _connectionsPerAddress.TryUpdate(remote, count - 1, count);
            if (done)
            {
                return;
            }
        }
    }

    private async Task RefuseAsync(SmtpListenerKind kind, NetworkStream stream, string reply)
    {
        _logger.LogInformation("SMTP connection refused: {Reply}", reply);

        // On port 465 the client expects TLS first; a plain reply would only confuse it.
        if (kind == SmtpListenerKind.ImplicitTls)
        {
            return;
        }

        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply + "\r\n"), timer.Token);
    }

    private async Task WaitForSessionsAsync()
    {
        Task[] open = _sessions.Values.ToArray();
        if (open.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(open).WaitAsync(ShutdownGrace);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Count} SMTP connections did not end in time.", open.Count(t => !t.IsCompleted));
        }
        catch (Exception)
        {
            // Sessions report their own failures.
        }
    }

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

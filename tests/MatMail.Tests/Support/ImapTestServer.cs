using System.Net;
using MailKit.Net.Imap;
using MailKit.Security;
using MatMail.Configuration;
using MatMail.MailServer.Imap;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests.Support;

/// <summary>
/// The IMAP server of a <see cref="TestHost"/>, running in-process on ephemeral loopback ports (plain/STARTTLS and implicit TLS)
/// with a self-signed certificate that is generated once per test run.
/// </summary>
public sealed class ImapTestServer : IAsyncDisposable
{
    public const string Password = "Test-Passw0rd!";

    private static readonly Lazy<CertificateProvider> SharedCertificates = new(CreateCertificates, LazyThreadSafetyMode.ExecutionAndPublication);

    private ImapTestServer(ImapServer server, ImapLoginThrottle throttle)
    {
        Server = server;
        Throttle = throttle;
    }

    public ImapServer Server { get; }

    public ImapLoginThrottle Throttle { get; }

    public int PlainPort => Server.PlainEndpoint!.Port;

    public int TlsPort => Server.TlsEndpoint!.Port;

    /// <summary>Starts the server; without a certificate no TLS port is opened and STARTTLS is not offered.</summary>
    public static async Task<ImapTestServer> StartAsync(TestHost host, bool withCertificate = true)
    {
        var throttle = new ImapLoginThrottle { FailureDelay = TimeSpan.FromMilliseconds(20) };
        var server = new ImapServer(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Config,
            host.Services.GetRequiredService<MailEventHub>(),
            throttle,
            host.Services.GetRequiredService<ActivityLogger>(),
            host.Services.GetRequiredService<ILogger<ImapServer>>(),
            withCertificate ? SharedCertificates.Value : null,
            new ImapListenOptions(IPAddress.Loopback, 0, 0));

        await server.StartAsync(CancellationToken.None);
        await server.Started;
        return new ImapTestServer(server, throttle);
    }

    /// <summary>A MailKit client connected over STARTTLS (or implicit TLS / plain) that accepts the self-signed certificate.</summary>
    public async Task<ImapClient> ConnectAsync(SecureSocketOptions security = SecureSocketOptions.StartTls)
    {
        var client = new ImapClient { Timeout = 15_000 };
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        int port = security == SecureSocketOptions.SslOnConnect ? TlsPort : PlainPort;
        await client.ConnectAsync("127.0.0.1", port, security);
        return client;
    }

    /// <summary>A MailKit client signed in as <paramref name="login"/>.</summary>
    public async Task<ImapClient> LoginAsync(string login = "alice", SecureSocketOptions security = SecureSocketOptions.StartTls)
    {
        ImapClient client = await ConnectAsync(security);
        await client.AuthenticateAsync(login, Password);
        return client;
    }

    /// <summary>A raw line-based client on the plain port (or the TLS port).</summary>
    public Task<RawImapClient> RawAsync(bool implicitTls = false)
        => RawImapClient.ConnectAsync(implicitTls ? TlsPort : PlainPort, implicitTls);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Server.StopAsync(timeout.Token);
        Server.Dispose();
    }

    private static CertificateProvider CreateCertificates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "matmail-imap-tests");
        Directory.CreateDirectory(directory);
        var config = new AppConfig();
        config.Server.Hostname = "mail.example.test";
        return new CertificateProvider(config, directory, NullLogger.Instance);
    }
}

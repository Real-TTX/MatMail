using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Configuration;
using MatMail.MailServer.Smtp;
using MatMail.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests.Support;

/// <summary>One self-signed certificate for all SMTP tests of a test run (RSA key generation per test would be slow).</summary>
internal static class TestCertificates
{
    private static readonly Lazy<CertificateProvider> Shared = new(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "matmail-test-certs-" + Guid.NewGuid().ToString("N")[..12]);
        var config = new AppConfig();
        config.Server.Hostname = "mail.example.test";
        config.Tls.CertificateDirectory = directory;
        var provider = new CertificateProvider(config, directory, NullLogger.Instance);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            provider.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp folder; the system cleans up eventually.
            }
        };
        return provider;
    });

    public static CertificateProvider Provider => Shared.Value;
}

/// <summary>An <see cref="SmtpServer"/> on free loopback ports (all three listeners), with test-friendly timeouts.</summary>
internal sealed class RunningSmtpServer : IAsyncDisposable
{
    private RunningSmtpServer(SmtpServer server, SmtpAuthThrottle throttle)
    {
        Server = server;
        Throttle = throttle;
    }

    public SmtpServer Server { get; }
    public SmtpAuthThrottle Throttle { get; }
    public int RelayPort => Server.BoundPorts[SmtpListenerKind.Relay];
    public int SubmissionPort => Server.BoundPorts[SmtpListenerKind.Submission];
    public int ImplicitTlsPort => Server.BoundPorts[SmtpListenerKind.ImplicitTls];

    public static async Task<RunningSmtpServer> StartAsync(TestHost host, bool withTls = true, TimeSpan? commandTimeout = null)
    {
        var options = new SmtpServerOptions
        {
            Endpoints = new[]
            {
                new SmtpEndpoint(SmtpListenerKind.Relay, IPAddress.Loopback, 0),
                new SmtpEndpoint(SmtpListenerKind.Submission, IPAddress.Loopback, 0),
                new SmtpEndpoint(SmtpListenerKind.ImplicitTls, IPAddress.Loopback, 0),
            },
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30),
            DataTimeout = TimeSpan.FromSeconds(30),
            AuthFailureDelay = TimeSpan.Zero,
        };

        var throttle = new SmtpAuthThrottle();
        var server = new SmtpServer(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Config,
            throttle,
            new SmtpActivityLog(host.Services.GetRequiredService<ActivityLogger>()),
            host.Services.GetRequiredService<ILogger<SmtpServer>>(),
            withTls ? TestCertificates.Provider : null,
            options);
        await server.StartAsync(CancellationToken.None);
        return new RunningSmtpServer(server, throttle);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(CancellationToken.None);
        Server.Dispose();
    }
}

/// <summary>A reply of the server: the code and every line (without the code).</summary>
internal sealed record SmtpReplyLines(int Code, IReadOnlyList<string> Lines)
{
    public string Text => string.Join("\n", Lines);

    public override string ToString() => $"{Code} {Text}";
}

/// <summary>A bare SMTP client over a socket, for checking exact replies, pipelining and the byte level of DATA.</summary>
internal sealed class RawSmtpClient : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpClient _tcp;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private Stream _stream;
    private int _start;
    private int _end;

    private RawSmtpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>Connects and reads the greeting.</summary>
    public static async Task<(RawSmtpClient Client, SmtpReplyLines Greeting)> ConnectAsync(int port, bool implicitTls = false)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var client = new RawSmtpClient(tcp);
        if (implicitTls)
        {
            await client.UpgradeAsync();
        }

        return (client, await client.ReadReplyAsync());
    }

    public bool IsEncrypted => _stream is SslStream;

    public async Task SendAsync(string text) => await SendBytesAsync(Encoding.UTF8.GetBytes(text));

    public async Task SendBytesAsync(byte[] bytes)
    {
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    public async Task<SmtpReplyLines> CommandAsync(string line)
    {
        await SendAsync(line + "\r\n");
        return await ReadReplyAsync();
    }

    /// <summary>EHLO and the extension keywords of the answer (first line = host name).</summary>
    public async Task<IReadOnlyList<string>> EhloAsync(string name = "client.test")
    {
        SmtpReplyLines reply = await CommandAsync("EHLO " + name);
        Assert.Equal(250, reply.Code);
        return reply.Lines;
    }

    public async Task StartTlsAsync()
    {
        SmtpReplyLines reply = await CommandAsync("STARTTLS");
        Assert.Equal(220, reply.Code);
        await UpgradeAsync();
    }

    /// <summary>MAIL FROM, RCPT TO each, DATA, message, end; returns the final reply (or the first refusal).</summary>
    public async Task<SmtpReplyLines> SendMailAsync(string from, IEnumerable<string> to, string message)
    {
        SmtpReplyLines reply = await CommandAsync($"MAIL FROM:<{from}>");
        if (reply.Code != 250)
        {
            return reply;
        }

        foreach (string recipient in to)
        {
            reply = await CommandAsync($"RCPT TO:<{recipient}>");
            if (reply.Code != 250)
            {
                return reply;
            }
        }

        reply = await CommandAsync("DATA");
        if (reply.Code != 354)
        {
            return reply;
        }

        await SendAsync(message.EndsWith("\r\n", StringComparison.Ordinal) ? message + ".\r\n" : message + "\r\n.\r\n");
        return await ReadReplyAsync();
    }

    public async Task<SmtpReplyLines> ReadReplyAsync()
    {
        var lines = new List<string>();
        while (true)
        {
            string? line = await ReadLineAsync() ?? throw new IOException($"The server closed the connection (after: {string.Join(" | ", lines)}).");
            if (line.Length < 4 || !int.TryParse(line[..3], out int code))
            {
                throw new InvalidDataException($"Not an SMTP reply: '{line}'");
            }

            lines.Add(line[4..]);
            if (line[3] == ' ')
            {
                return new SmtpReplyLines(code, lines);
            }
        }
    }

    /// <summary>True when the server closed the connection (nothing more to read).</summary>
    public async Task<bool> IsClosedAsync()
    {
        try
        {
            return await ReadLineAsync() is null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task UpgradeAsync()
    {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "mail.example.test",
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        _stream = ssl;
        _start = _end = 0;
    }

    private async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                _start = newline + 1;
                return line;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            using var timer = new CancellationTokenSource(ReadTimeout);
            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), timer.Token);
            if (read == 0)
            {
                return null;
            }

            _end += read;
        }
    }
}

/// <summary>MailKit clients for the tests: any certificate is accepted.</summary>
internal static class TestMailClients
{
    public const string Password = "Test-Passw0rd!";

    public static async Task<SmtpClient> ConnectAsync(int port, SecureSocketOptions security)
    {
        var client = new SmtpClient
        {
            ServerCertificateValidationCallback = (_, _, _, _) => true,
            CheckCertificateRevocation = false,
            LocalDomain = "client.test",
            Timeout = 15000,
        };
        await client.ConnectAsync("127.0.0.1", port, security);
        return client;
    }

    /// <summary>Submission port, STARTTLS, signed in.</summary>
    public static async Task<SmtpClient> SignInAsync(int submissionPort, string login)
    {
        SmtpClient client = await ConnectAsync(submissionPort, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(login, Password);
        return client;
    }
}

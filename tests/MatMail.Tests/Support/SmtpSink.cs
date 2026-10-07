using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MatMail.Tests.Support;

/// <summary>One message the sink accepted.</summary>
internal sealed record SinkMessage(string? Helo, string? AuthUser, string MailFrom, IReadOnlyList<string> Recipients, string Data);

/// <summary>
/// A scripted SMTP server standing in for a provider's SMTP or a recipient's mail server: records every accepted message and
/// answers MAIL FROM, RCPT TO and the end of DATA the way the test says (null = accept).
/// </summary>
internal sealed class SmtpSink : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<SinkMessage> _messages = new();
    private readonly ConcurrentQueue<string> _helos = new();
    private readonly Task _acceptLoop;

    private SmtpSink()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public IReadOnlyList<SinkMessage> Messages => _messages.ToArray();

    /// <summary>The EHLO names clients used.</summary>
    public IReadOnlyList<string> Helos => _helos.ToArray();

    /// <summary>Reply to MAIL FROM for an address; null = "250".</summary>
    public Func<string, string?> MailFromReply { get; set; } = _ => null;

    /// <summary>Reply to RCPT TO for an address; null = "250".</summary>
    public Func<string, string?> RecipientReply { get; set; } = _ => null;

    /// <summary>Reply after the message data; null = "250".</summary>
    public Func<string?> DataReply { get; set; } = () => null;

    public static SmtpSink Start() => new();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Stopped.
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await ConverseAsync(client.GetStream());
            }
            catch (Exception)
            {
                // The client went away.
            }
        }
    }

    private async Task ConverseAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        string? helo = null;
        string? authUser = null;
        string? from = null;
        var recipients = new List<string>();

        await writer.WriteLineAsync("220 sink.test ESMTP");
        while (await reader.ReadLineAsync() is string line)
        {
            string upper = line.ToUpperInvariant();
            if (upper.StartsWith("EHLO ", StringComparison.Ordinal))
            {
                helo = line[5..].Trim();
                _helos.Enqueue(helo);
                await writer.WriteAsync("250-sink.test\r\n250-PIPELINING\r\n250-8BITMIME\r\n250-AUTH PLAIN LOGIN\r\n250 SIZE 104857600\r\n");
            }
            else if (upper.StartsWith("HELO ", StringComparison.Ordinal))
            {
                helo = line[5..].Trim();
                _helos.Enqueue(helo);
                await writer.WriteLineAsync("250 sink.test");
            }
            else if (upper.StartsWith("AUTH PLAIN ", StringComparison.Ordinal))
            {
                string[] fields = Encoding.UTF8.GetString(Convert.FromBase64String(line[11..].Trim())).Split('\0');
                authUser = fields.Length == 3 ? fields[1] : null;
                await writer.WriteLineAsync("235 2.7.0 Authentication successful");
            }
            else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal))
            {
                string address = PathOf(line[10..]);
                string? reply = MailFromReply(address);
                if (reply is null)
                {
                    from = address;
                    recipients.Clear();
                }

                await writer.WriteLineAsync(reply ?? "250 2.1.0 Ok");
            }
            else if (upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
            {
                string address = PathOf(line[8..]);
                string? reply = from is null ? "503 5.5.1 Error: need MAIL command" : RecipientReply(address);
                if (reply is null)
                {
                    recipients.Add(address);
                }

                await writer.WriteLineAsync(reply ?? "250 2.1.5 Ok");
            }
            else if (upper == "DATA")
            {
                if (from is null || recipients.Count == 0)
                {
                    await writer.WriteLineAsync("554 5.5.1 Error: no valid recipients");
                    continue;
                }

                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is string dataLine && dataLine != ".")
                {
                    data.Append(dataLine.StartsWith('.') ? dataLine[1..] : dataLine).Append("\r\n");
                }

                string? reply = DataReply();
                if (reply is null)
                {
                    _messages.Enqueue(new SinkMessage(helo, authUser, from, recipients.ToList(), data.ToString()));
                }

                await writer.WriteLineAsync(reply ?? "250 2.0.0 Ok: queued");
                from = null;
                recipients.Clear();
            }
            else if (upper == "RSET")
            {
                from = null;
                recipients.Clear();
                await writer.WriteLineAsync("250 2.0.0 Ok");
            }
            else if (upper == "NOOP")
            {
                await writer.WriteLineAsync("250 2.0.0 Ok");
            }
            else if (upper == "QUIT")
            {
                await writer.WriteLineAsync("221 2.0.0 Bye");
                return;
            }
            else
            {
                await writer.WriteLineAsync("502 5.5.2 Error: command not recognized");
            }
        }
    }

    /// <summary>"&lt;a@b&gt; SIZE=1" → "a@b".</summary>
    private static string PathOf(string argument)
    {
        string value = argument.Trim();
        int close = value.IndexOf('>');
        return value.StartsWith('<') && close > 0 ? value[1..close] : value.Split(' ')[0];
    }
}

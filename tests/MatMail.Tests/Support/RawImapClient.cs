using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MatMail.Tests.Support;

/// <summary>
/// A bare IMAP client for tests that pin the exact protocol syntax: it sends text as given and reads responses line by line.
/// A response line that ends with a literal marker ({n}) is returned together with the literal and the rest of the line.
/// </summary>
public sealed class RawImapClient : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpClient _tcp;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private Stream _stream;
    private int _start;
    private int _end;

    private RawImapClient(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<RawImapClient> ConnectAsync(int port, bool implicitTls = false)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        Stream stream = tcp.GetStream();
        if (implicitTls)
        {
            stream = await AuthenticateAsync(stream);
        }

        return new RawImapClient(tcp, stream);
    }

    /// <summary>Sends text exactly as given (the caller adds CRLF).</summary>
    public async Task SendAsync(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    public Task SendAsync(byte[] bytes) => _stream.WriteAsync(bytes).AsTask();

    /// <summary>Sends "tag command" and returns every line up to and including the tagged response.</summary>
    public async Task<List<string>> CommandAsync(string tag, string command)
    {
        await SendAsync($"{tag} {command}\r\n");
        return await ReadUntilTaggedAsync(tag);
    }

    public async Task<List<string>> ReadUntilTaggedAsync(string tag)
    {
        var lines = new List<string>();
        while (true)
        {
            string line = await ReadResponseAsync();
            lines.Add(line);
            if (line.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                return lines;
            }
        }
    }

    /// <summary>One response line; literals are read with it (their data appears inline, CRLFs included).</summary>
    public async Task<string> ReadResponseAsync()
    {
        var response = new StringBuilder();
        while (true)
        {
            string line = await ReadLineAsync();
            response.Append(line);
            int open = line.LastIndexOf('{');
            if (!line.EndsWith('}') || open < 0 || !int.TryParse(line.AsSpan(open + 1, line.Length - open - 2), out int size))
            {
                return response.ToString();
            }

            response.Append("\r\n").Append(Encoding.UTF8.GetString(await ReadBytesAsync(size)));
        }
    }

    /// <summary>One line without its CRLF.</summary>
    public async Task<string> ReadLineAsync()
    {
        var line = new List<byte>();
        while (true)
        {
            while (_start < _end)
            {
                byte value = _buffer[_start++];
                if (value == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.UTF8.GetString(line.ToArray());
                }

                line.Add(value);
            }

            await FillAsync();
        }
    }

    /// <summary>True when the server closed the connection (reads end without data).</summary>
    public async Task<bool> IsClosedAsync()
    {
        try
        {
            if (_start < _end)
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(ReadTimeout);
            return await _stream.ReadAsync(_buffer, timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Upgrades to TLS after the server accepted STARTTLS.</summary>
    public async Task StartTlsAsync()
    {
        _start = 0;
        _end = 0;
        _stream = await AuthenticateAsync(_stream);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private async Task<byte[]> ReadBytesAsync(int count)
    {
        var result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            if (_start == _end)
            {
                await FillAsync();
            }

            int take = Math.Min(count - filled, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, filled, take);
            _start += take;
            filled += take;
        }

        return result;
    }

    private async Task FillAsync()
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        int read = await _stream.ReadAsync(_buffer, timeout.Token);
        if (read == 0)
        {
            throw new EndOfStreamException("The server closed the connection.");
        }

        _start = 0;
        _end = read;
    }

    private static async Task<Stream> AuthenticateAsync(Stream stream)
    {
        var ssl = new SslStream(stream, false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");
        return ssl;
    }
}

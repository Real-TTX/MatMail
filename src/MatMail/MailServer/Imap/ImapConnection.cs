using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>A command line exceeded the allowed length; the connection is closed.</summary>
internal sealed class ImapLineTooLongException : Exception
{
    public ImapLineTooLongException() : base("The command line is too long.")
    {
    }
}

/// <summary>
/// The byte stream of one client connection: buffered reading of lines and literals, buffered writing, and the switch to TLS
/// (STARTTLS). Not thread-safe; a session reads and writes from one logical flow (IDLE keeps one pending read at most).
/// </summary>
internal sealed class ImapConnection : IAsyncDisposable
{
    public const int FlushThreshold = 64 * 1024;

    private static readonly SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    private readonly byte[] _input = new byte[16 * 1024];
    private readonly MemoryStream _output = new();
    private Stream _stream;
    private int _inputStart;
    private int _inputEnd;
    private byte[] _line = new byte[1024];

    public ImapConnection(Stream stream, string remoteIp, bool isTls)
    {
        _stream = stream;
        RemoteIp = remoteIp;
        IsTls = isTls;
    }

    public string RemoteIp { get; }

    public bool IsTls { get; private set; }

    /// <summary>Bytes written but not yet sent.</summary>
    public long PendingOutput => _output.Length;

    /// <summary>Performs the server side of a TLS handshake on a fresh connection (implicit TLS port).</summary>
    public static async Task<SslStream> AuthenticateAsync(Stream stream, X509Certificate2 certificate, CancellationToken cancel)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = AllowedProtocols,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancel);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Switches to TLS. Whatever the client sent after the STARTTLS command line is discarded first, so nothing sent in the
    /// clear can be injected into the encrypted session.
    /// </summary>
    public async Task StartTlsAsync(X509Certificate2 certificate, CancellationToken cancel)
    {
        _inputStart = 0;
        _inputEnd = 0;
        _stream = await AuthenticateAsync(_stream, certificate, cancel);
        IsTls = true;
    }

    /// <summary>
    /// Reads one line without its line end (CRLF or a bare LF). Returns null when the client closed the connection.
    /// </summary>
    public async Task<byte[]?> ReadLineAsync(int maxLength, CancellationToken cancel)
    {
        int length = 0;
        while (true)
        {
            int available = _inputEnd - _inputStart;
            int newline = available > 0 ? Array.IndexOf(_input, (byte)'\n', _inputStart, available) : -1;
            int take = newline >= 0 ? newline - _inputStart : available;
            if (length + take > maxLength)
            {
                throw new ImapLineTooLongException();
            }

            AppendToLine(_input.AsSpan(_inputStart, take), ref length);
            if (newline >= 0)
            {
                _inputStart = newline + 1;
                if (length > 0 && _line[length - 1] == '\r')
                {
                    length--;
                }

                return _line.AsSpan(0, length).ToArray();
            }

            _inputStart = 0;
            _inputEnd = 0;
            int read = await _stream.ReadAsync(_input.AsMemory(), cancel);
            if (read == 0)
            {
                return null;
            }

            _inputEnd = read;
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (a literal).</summary>
    public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancel)
    {
        var result = new byte[count];
        int filled = Math.Min(count, _inputEnd - _inputStart);
        Buffer.BlockCopy(_input, _inputStart, result, 0, filled);
        _inputStart += filled;

        while (filled < count)
        {
            int read = await _stream.ReadAsync(result.AsMemory(filled, count - filled), cancel);
            if (read == 0)
            {
                throw new EndOfStreamException("The client closed the connection in the middle of a literal.");
            }

            filled += read;
        }

        return result;
    }

    /// <summary>Reads and drops <paramref name="count"/> bytes (a literal that is refused).</summary>
    public async Task SkipBytesAsync(long count, CancellationToken cancel)
    {
        long remaining = count;
        int buffered = (int)Math.Min(remaining, _inputEnd - _inputStart);
        _inputStart += buffered;
        remaining -= buffered;

        while (remaining > 0)
        {
            int read = await _stream.ReadAsync(_input.AsMemory(0, (int)Math.Min(_input.Length, remaining)), cancel);
            if (read == 0)
            {
                throw new EndOfStreamException("The client closed the connection in the middle of a literal.");
            }

            remaining -= read;
        }

        _inputStart = 0;
        _inputEnd = 0;
    }

    public void Write(string text)
    {
        int count = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (count <= 4096)
        {
            Span<byte> buffer = stackalloc byte[count];
            int written = Encoding.UTF8.GetBytes(text, buffer);
            _output.Write(buffer[..written]);
            return;
        }

        _output.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Write(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    /// <summary>Writes a block of data; large blocks go straight to the stream instead of through the buffer.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel)
    {
        if (bytes.Length < FlushThreshold)
        {
            _output.Write(bytes.Span);
            return;
        }

        await SendBufferedAsync(cancel);
        await _stream.WriteAsync(bytes, cancel);
    }

    public async Task FlushAsync(CancellationToken cancel)
    {
        await SendBufferedAsync(cancel);
        await _stream.FlushAsync(cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        await _output.DisposeAsync();
    }

    private async Task SendBufferedAsync(CancellationToken cancel)
    {
        if (_output.Length == 0)
        {
            return;
        }

        await _stream.WriteAsync(_output.GetBuffer().AsMemory(0, (int)_output.Length), cancel);
        _output.SetLength(0);
    }

    private void AppendToLine(ReadOnlySpan<byte> bytes, ref int length)
    {
        if (length + bytes.Length > _line.Length)
        {
            Array.Resize(ref _line, Math.Max(_line.Length * 2, length + bytes.Length));
        }

        bytes.CopyTo(_line.AsSpan(length));
        length += bytes.Length;
    }
}

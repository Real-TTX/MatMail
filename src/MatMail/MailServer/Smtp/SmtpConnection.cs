using System.Text;

namespace MatMail.MailServer.Smtp;

internal enum SmtpLineStatus
{
    Ok,
    TooLong,
    Closed,
}

/// <summary>One command line from the client (without the line end).</summary>
internal readonly record struct SmtpLine(SmtpLineStatus Status, string Text);

internal enum SmtpDataStatus
{
    Complete,
    TooBig,
    Closed,
}

/// <summary>
/// The byte level of one SMTP connection: buffered reading of command lines and of the DATA block (dot-unstuffing, line ends
/// normalised to CRLF, size limit) and buffered writing of replies. Pending replies are sent right before the server waits for
/// more input, so pipelined commands get their replies in one go (RFC 2920). The stream can be replaced once TLS is up.
/// </summary>
internal sealed class SmtpConnection : IAsyncDisposable
{
    private const int BufferSize = 32 * 1024;

    private readonly byte[] _input = new byte[BufferSize];
    private readonly MemoryStream _output = new();
    private Stream _stream;
    private int _start;
    private int _end;

    public SmtpConnection(Stream stream) => _stream = stream;

    public Stream Stream => _stream;

    /// <summary>Queues one reply line; CRLF is appended. Replies are ASCII: anything else becomes '?', so no reply can break the protocol.</summary>
    public void Write(string line)
    {
        foreach (char c in line)
        {
            _output.WriteByte(c is >= ' ' and <= '~' ? (byte)c : (byte)'?');
        }

        _output.WriteByte((byte)'\r');
        _output.WriteByte((byte)'\n');
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancel)
    {
        if (_output.Length == 0)
        {
            return;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(timeout);
        try
        {
            await _stream.WriteAsync(_output.GetBuffer().AsMemory(0, (int)_output.Length), timer.Token);
            await _stream.FlushAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The client did not take the reply in time.");
        }

        _output.SetLength(0);
    }

    /// <summary>
    /// Switches to another stream (TLS). Input that was buffered from the old stream is dropped on purpose: commands a client
    /// pipelined behind STARTTLS in plain text must never be executed inside the encrypted session.
    /// </summary>
    public void ReplaceStream(Stream stream)
    {
        _stream = stream;
        _start = _end = 0;
        _output.SetLength(0);
    }

    /// <summary>Reads one line. Longer lines than <paramref name="maxLength"/> are skipped completely and reported as too long.</summary>
    public async Task<SmtpLine> ReadLineAsync(int maxLength, TimeSpan timeout, CancellationToken cancel)
    {
        bool tooLong = false;
        while (true)
        {
            int newline = Array.IndexOf(_input, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                int length = newline - _start;
                string text = tooLong || length > maxLength ? string.Empty : Decode(_input.AsSpan(_start, length));
                bool lineTooLong = tooLong || length > maxLength;
                _start = newline + 1;
                return lineTooLong ? new SmtpLine(SmtpLineStatus.TooLong, string.Empty) : new SmtpLine(SmtpLineStatus.Ok, text);
            }

            if (_end - _start > maxLength)
            {
                // Far too long for a command: forget what we have and skip to the end of the line.
                tooLong = true;
                _start = _end = 0;
            }

            if (!await FillAsync(timeout, cancel))
            {
                return new SmtpLine(SmtpLineStatus.Closed, string.Empty);
            }
        }
    }

    /// <summary>
    /// Reads the message after DATA up to the terminating "CRLF.CRLF" into <paramref name="target"/>. Only a dot line that ends
    /// with CRLF and follows a line that ended with CRLF ends the data (bare LF variants are message content: no SMTP smuggling).
    /// Leading dots are unstuffed, bare LF line ends become CRLF. Beyond <paramref name="maxBytes"/> the data is read but dropped.
    /// </summary>
    public async Task<SmtpDataStatus> ReadDataAsync(MemoryStream target, long maxBytes, TimeSpan timeout, CancellationToken cancel)
    {
        bool lineStart = true;
        bool previousLineCrlf = true;
        bool pendingCr = false;
        bool tooBig = false;

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (tooBig || bytes.Length == 0)
            {
                return;
            }

            if (target.Length + bytes.Length > maxBytes)
            {
                tooBig = true;
                return;
            }

            target.Write(bytes);
        }

        while (true)
        {
            if (_start == _end && !await FillAsync(timeout, cancel))
            {
                return SmtpDataStatus.Closed;
            }

            if (lineStart)
            {
                lineStart = false;
                if (_input[_start] != (byte)'.')
                {
                    continue;
                }

                if (!await EnsureAsync(3, timeout, cancel))
                {
                    return SmtpDataStatus.Closed;
                }

                if (previousLineCrlf && _input[_start + 1] == (byte)'\r' && _input[_start + 2] == (byte)'\n')
                {
                    _start += 3;
                    return tooBig ? SmtpDataStatus.TooBig : SmtpDataStatus.Complete;
                }

                // Dot-unstuffing: the client doubled a leading dot.
                _start++;
                continue;
            }

            int available = _end - _start;
            int newline = Array.IndexOf(_input, (byte)'\n', _start, available);
            if (newline < 0)
            {
                // No line end yet: keep a trailing CR back, it may be the first half of CRLF.
                if (pendingCr)
                {
                    Append("\r"u8);
                    pendingCr = false;
                }

                int take = available;
                if (_input[_end - 1] == (byte)'\r')
                {
                    take--;
                    pendingCr = true;
                }

                Append(_input.AsSpan(_start, take));
                _start = _end;
                continue;
            }

            int contentLength = newline - _start;
            bool crlf;
            if (contentLength > 0)
            {
                if (pendingCr)
                {
                    Append("\r"u8);
                    pendingCr = false;
                }

                crlf = _input[newline - 1] == (byte)'\r';
                Append(_input.AsSpan(_start, crlf ? contentLength - 1 : contentLength));
            }
            else
            {
                crlf = pendingCr;
                pendingCr = false;
            }

            Append("\r\n"u8);
            _start = newline + 1;
            previousLineCrlf = crlf;
            lineStart = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        await _output.DisposeAsync();
    }

    private async Task<bool> EnsureAsync(int count, TimeSpan timeout, CancellationToken cancel)
    {
        while (_end - _start < count)
        {
            if (!await FillAsync(timeout, cancel))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads more input (after sending the pending replies: the client may be waiting for them). False at end of stream.</summary>
    private async Task<bool> FillAsync(TimeSpan timeout, CancellationToken cancel)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_input, _start, _input, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _input.Length)
        {
            throw new InvalidOperationException("The SMTP input buffer is full.");
        }

        await FlushAsync(timeout, cancel);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(timeout);
        int read;
        try
        {
            read = await _stream.ReadAsync(_input.AsMemory(_end), timer.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The client sent nothing in time.");
        }

        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }

    private static string Decode(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return Encoding.UTF8.GetString(line);
    }
}

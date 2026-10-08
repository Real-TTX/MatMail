using System.Security.Cryptography;

namespace MatMail.Backup;

/// <summary>Passes what is written through and keeps count and SHA-256 of it.</summary>
public sealed class HashingWriteStream(Stream inner, bool leaveOpen = true) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _result;

    public long BytesWritten { get; private set; }

    /// <summary>The checksum of everything written so far (lower-case hex); the stream may be written to again afterwards.</summary>
    public string Sha256Hex => _result ?? Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _result ??= Convert.ToHexStringLower(_hash.GetHashAndReset());
            _hash.Dispose();
            if (!leaveOpen)
            {
                inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Passes what is read through and checks it at the end against the size and the SHA-256 the manifest promised: a part of a backup
/// that is damaged is never taken for good, it fails when its last byte has been read.
/// </summary>
public sealed class VerifyingReadStream(Stream inner, string name, long expectedBytes, string expectedSha256, bool leaveOpen = false) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;
    private bool _checked;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedBytes;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;   // asking for nothing is not the end of the part
        }

        int read = inner.Read(buffer);
        Account(buffer[..read], read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int read = await inner.ReadAsync(buffer, cancellationToken);
        Account(buffer.Span[..read], read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(ReadOnlySpan<byte> data, int read)
    {
        if (read > 0)
        {
            _hash.AppendData(data);
            _read += read;
            return;
        }

        if (_checked)
        {
            return;
        }

        _checked = true;
        string actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
        if (_read != expectedBytes)
        {
            throw new BackupCorruptException($"“{name}” has {_read} bytes, the backup says {expectedBytes}: the backup is damaged or cut off.");
        }

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupCorruptException($"“{name}” does not match its checksum: the backup is damaged.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!leaveOpen)
            {
                inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>Reads the parts of a table one after the other as if they were one stream.</summary>
public sealed class ConcatenatedReadStream(IEnumerable<Func<Stream>> parts) : Stream
{
    private readonly IEnumerator<Func<Stream>> _parts = parts.GetEnumerator();
    private Stream? _current;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is null)
            {
                if (!_parts.MoveNext())
                {
                    return 0;
                }

                _current = _parts.Current();
            }

            int read = _current.Read(buffer);
            if (read > 0)
            {
                return read;
            }

            _current.Dispose();
            _current = null;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is null)
            {
                if (!_parts.MoveNext())
                {
                    return 0;
                }

                _current = _parts.Current();
            }

            int read = await _current.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            await _current.DisposeAsync();
            _current = null;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _parts.Dispose();
        }

        base.Dispose(disposing);
    }
}

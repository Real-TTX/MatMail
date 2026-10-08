using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MatMail.Backup;

/// <summary>
/// The container of an encrypted backup: the whole archive, encrypted with AES-256-GCM in chunks, the key derived from a passphrase
/// (PBKDF2-HMAC-SHA256). Every chunk is authenticated together with the header, its number and a flag that marks the last one, so a
/// changed, reordered or cut-off backup is recognised, and the passphrase is checked before the first chunk is read.
///
/// <code>
/// header (50 bytes): "MMBK" | version 1 | kdf 1 | iterations (4) | salt (16) | nonce prefix (4) | chunk size (4) | key check (16)
/// chunks: flag (1: 1 = last) | length (4) | ciphertext (length) | tag (16)      nonce = prefix | chunk number (8)
/// </code>
///
/// All chunks but the last have the size from the header, so the file can be read with random access (see <see cref="Decrypt"/>).
/// </summary>
public static class BackupEncryption
{
    public const int HeaderBytes = 50;
    public const int DefaultChunkBytes = 1024 * 1024;
    public const int DefaultIterations = 600_000;
    private const int TagBytes = 16;
    private static readonly byte[] Magic = "MMBK"u8.ToArray();

    /// <summary>Whether the start of a file is the header of an encrypted backup (a plain backup is a zip file: it starts with "PK").</summary>
    public static bool LooksEncrypted(ReadOnlySpan<byte> start) => start.Length >= Magic.Length && start[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Wraps a stream so that what is written to it arrives encrypted; disposing it writes the last chunk (the inner stream stays open).</summary>
    public static Stream Encrypt(Stream output, string passphrase, int iterations = DefaultIterations, int chunkBytes = DefaultChunkBytes)
        => new EncryptingStream(output, passphrase, iterations, chunkBytes);

    /// <summary>
    /// Wraps an encrypted file so that reading it gives the plain content, with random access (a zip archive can be opened on it
    /// without a decrypted copy); the inner stream must be seekable and stays open. A wrong passphrase, a file that is cut off and one
    /// that was changed are recognised when the stream is opened (the last chunk is checked) or when the damaged chunk is read.
    /// </summary>
    public static Stream Decrypt(Stream input, string passphrase) => new DecryptingStream(input, passphrase);

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] KeyCheck(byte[] key) => HMACSHA256.HashData(key, "MatMail backup key check"u8)[..16];

    private static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, long chunk)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], chunk);
    }

    private static byte[] AssociatedData(byte[] header, long chunk, byte flag)
    {
        byte[] data = new byte[header.Length + 9];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(header.Length), chunk);
        data[^1] = flag;
        return data;
    }

    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _header;
        private readonly byte[] _prefix;
        private readonly AesGcm _aes;
        private readonly byte[] _buffer;
        private int _filled;
        private long _chunk;
        private bool _finished;

        public EncryptingStream(Stream output, string passphrase, int iterations, int chunkBytes)
        {
            ArgumentException.ThrowIfNullOrEmpty(passphrase);
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1024);
            _output = output;
            _buffer = new byte[chunkBytes];
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            _prefix = RandomNumberGenerator.GetBytes(4);
            byte[] key = DeriveKey(passphrase, salt, iterations);
            _aes = new AesGcm(key, TagBytes);

            _header = new byte[HeaderBytes];
            Magic.CopyTo(_header, 0);
            _header[4] = 1;   // version
            _header[5] = 1;   // PBKDF2-HMAC-SHA256
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(6), iterations);
            salt.CopyTo(_header, 10);
            _prefix.CopyTo(_header, 26);
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(30), chunkBytes);
            KeyCheck(key).CopyTo(_header, 34);
            _output.Write(_header);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                data = data[take..];
                if (_filled == _buffer.Length)
                {
                    _output.Write(Seal(last: false));
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            while (!data.IsEmpty)
            {
                int take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsMemory(_filled));
                _filled += take;
                data = data[take..];
                if (_filled == _buffer.Length)
                {
                    byte[] record = Seal(last: false);
                    await _output.WriteAsync(record, cancellationToken);
                }
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>The next record (flag, length, ciphertext, tag) for what is in the buffer; empties the buffer.</summary>
        private byte[] Seal(bool last)
        {
            byte flag = (byte)(last ? 1 : 0);
            byte[] record = new byte[5 + _filled + TagBytes];
            record[0] = flag;
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(1), _filled);
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _prefix, _chunk);
            _aes.Encrypt(nonce, _buffer.AsSpan(0, _filled), record.AsSpan(5, _filled), record.AsSpan(5 + _filled, TagBytes), AssociatedData(_header, _chunk, flag));
            _chunk++;
            _filled = 0;
            return record;
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _output.Write(Seal(last: true));
            _output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Finish();
                _aes.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_finished)
            {
                _finished = true;
                await _output.WriteAsync(Seal(last: true));
                await _output.FlushAsync();
            }

            _aes.Dispose();
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads an encrypted backup with random access: every chunk but the last has the size from the header, so the position of a
    /// chunk follows from its number and the size of the file, and the zip reader can jump around in it.
    /// </summary>
    private sealed class DecryptingStream : Stream
    {
        private const int RecordOverhead = 5 + TagBytes;

        private readonly Stream _input;
        private readonly byte[] _header;
        private readonly byte[] _prefix;
        private readonly AesGcm _aes;
        private readonly int _chunkBytes;
        private readonly long _chunkCount;
        private readonly long _lastLength;
        private readonly long _length;
        private readonly byte[] _plain;
        private long _plainChunk = -1;
        private int _plainLength;
        private long _position;

        public DecryptingStream(Stream input, string passphrase)
        {
            if (!input.CanSeek)
            {
                throw new ArgumentException("An encrypted backup is read from a file: the stream must be seekable.", nameof(input));
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                throw new BackupPassphraseException("The backup is encrypted: a passphrase is needed.");
            }

            _input = input;
            _header = new byte[HeaderBytes];
            _input.Seek(0, SeekOrigin.Begin);
            ReadInput(_header);
            if (!LooksEncrypted(_header) || _header[4] != 1 || _header[5] != 1)
            {
                throw new BackupCorruptException("This is not an encrypted MatMail backup (or one of a newer format).");
            }

            int iterations = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(6));
            _chunkBytes = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(30));
            if (iterations is < 1000 or > 50_000_000 || _chunkBytes is < 1024 or > 64 * 1024 * 1024)
            {
                throw new BackupCorruptException("The header of the encrypted backup is damaged.");
            }

            _prefix = _header[26..30];
            byte[] key = DeriveKey(passphrase, _header.AsSpan(10, 16), iterations);
            if (!CryptographicOperations.FixedTimeEquals(KeyCheck(key), _header.AsSpan(34, 16)))
            {
                throw new BackupPassphraseException("The passphrase is wrong.");
            }

            _aes = new AesGcm(key, TagBytes);
            _plain = new byte[_chunkBytes];

            // The file is the header and the chunks; the last one is the only one that is not full, and it always exists (empty when
            // the content is a multiple of the chunk size). Size and number of chunks follow from the length of the file.
            long body = _input.Length - HeaderBytes;
            if (body < RecordOverhead)
            {
                throw new BackupCorruptException("The encrypted backup is cut off.");
            }

            long record = _chunkBytes + (long)RecordOverhead;
            long full = (body - RecordOverhead) / record;
            _lastLength = body - RecordOverhead - full * record;
            _chunkCount = full + 1;
            _length = full * _chunkBytes + _lastLength;

            // The end first: a backup that was cut off or has something appended is recognised before anything is used.
            Load(_chunkCount - 1);
        }

        private void ReadInput(Span<byte> buffer)
        {
            try
            {
                _input.ReadExactly(buffer);
            }
            catch (EndOfStreamException ex)
            {
                throw new BackupCorruptException("The encrypted backup is cut off.", ex);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _length)
            {
                return 0;
            }

            long chunk = _position / _chunkBytes;
            int offset = (int)(_position % _chunkBytes);
            if (chunk != _plainChunk)
            {
                Load(chunk);
            }

            int take = Math.Min(buffer.Length, _plainLength - offset);
            _plain.AsSpan(offset, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        /// <summary>Reads, checks and decrypts one chunk into the buffer.</summary>
        private void Load(long chunk)
        {
            bool last = chunk == _chunkCount - 1;
            int expectedLength = last ? (int)_lastLength : _chunkBytes;
            byte expectedFlag = (byte)(last ? 1 : 0);

            _plainChunk = -1;
            _input.Seek(HeaderBytes + chunk * (_chunkBytes + (long)RecordOverhead), SeekOrigin.Begin);
            Span<byte> head = stackalloc byte[5];
            ReadInput(head);
            if (head[0] != expectedFlag || BinaryPrimitives.ReadInt32BigEndian(head[1..]) != expectedLength)
            {
                throw new BackupCorruptException("The encrypted backup is damaged, cut off or was changed (the chunks do not add up).");
            }

            byte[] cipher = new byte[expectedLength];
            Span<byte> tag = stackalloc byte[TagBytes];
            ReadInput(cipher);
            ReadInput(tag);

            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _prefix, chunk);
            try
            {
                _aes.Decrypt(nonce, cipher, tag, _plain.AsSpan(0, expectedLength), AssociatedData(_header, chunk, expectedFlag));
            }
            catch (CryptographicException ex)
            {
                throw new BackupCorruptException("The encrypted backup is damaged or was changed (a chunk does not authenticate).", ex);
            }

            _plainChunk = chunk;
            _plainLength = expectedLength;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

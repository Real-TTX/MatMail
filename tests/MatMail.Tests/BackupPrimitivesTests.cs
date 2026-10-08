using System.Security.Cryptography;
using MatMail.Backup;
using MatMail.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests;

public class HashingStreamTests
{
    [Fact]
    public void The_writer_counts_and_hashes_what_passes_through()
    {
        byte[] data = RandomNumberGenerator.GetBytes(50_000);
        var sink = new MemoryStream();
        var hashing = new HashingWriteStream(sink);
        hashing.Write(data, 0, 20_000);
        hashing.Write(data.AsSpan(20_000));
        string before = hashing.Sha256Hex;
        hashing.Dispose();

        Assert.Equal(data.Length, hashing.BytesWritten);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hashing.Sha256Hex);
        Assert.Equal(before, hashing.Sha256Hex);
        Assert.Equal(data, sink.ToArray());
    }

    [Fact]
    public async Task A_part_that_matches_its_manifest_reads_through()
    {
        byte[] data = RandomNumberGenerator.GetBytes(70_000);
        await using var stream = new VerifyingReadStream(new MemoryStream(data), "part", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task A_changed_part_fails_at_its_end()
    {
        byte[] data = RandomNumberGenerator.GetBytes(10_000);
        string sha = Convert.ToHexStringLower(SHA256.HashData(data));
        data[5_000] ^= 1;
        await using var stream = new VerifyingReadStream(new MemoryStream(data), "part", data.Length, sha);

        await Assert.ThrowsAsync<BackupCorruptException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_part_of_the_wrong_size_fails(int difference)
    {
        byte[] data = RandomNumberGenerator.GetBytes(10_000);
        string sha = Convert.ToHexStringLower(SHA256.HashData(data));
        await using var stream = new VerifyingReadStream(new MemoryStream(data), "part", data.Length + difference, sha);

        await Assert.ThrowsAsync<BackupCorruptException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Asking_for_no_bytes_is_not_the_end_of_a_part()
    {
        byte[] data = [1, 2, 3];
        await using var stream = new VerifyingReadStream(new MemoryStream(data), "part", 99, "00");

        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(3, await stream.ReadAsync(new byte[10]));
    }

    [Fact]
    public async Task Parts_are_read_one_after_the_other_as_one_stream()
    {
        var opened = new List<int>();
        Func<Stream> Part(int number, byte value) => () =>
        {
            opened.Add(number);
            return new MemoryStream(Enumerable.Repeat(value, 1000 * number).ToArray());
        };

        await using var joined = new ConcatenatedReadStream([Part(1, 1), Part(2, 2), Part(3, 3)]);
        var copy = new MemoryStream();
        await joined.CopyToAsync(copy);

        byte[] all = copy.ToArray();
        Assert.Equal(6000, all.Length);
        Assert.All(all[..1000], b => Assert.Equal(1, b));
        Assert.All(all[1000..3000], b => Assert.Equal(2, b));
        Assert.All(all[3000..], b => Assert.Equal(3, b));
        Assert.Equal([1, 2, 3], opened);
    }
}

public class BackupEncryptionTests
{
    // Few iterations and small chunks keep the tests quick; the format is the same as with the defaults.
    private const int Iterations = 1000;
    private const int Chunk = 1024;

    private static byte[] Encrypt(byte[] plain, string passphrase = "correct horse")
    {
        var output = new MemoryStream();
        using (Stream encrypting = BackupEncryption.Encrypt(output, passphrase, Iterations, Chunk))
        {
            encrypting.Write(plain);
        }

        return output.ToArray();
    }

    private static byte[] ReadAll(byte[] encrypted, string passphrase = "correct horse")
    {
        using Stream decrypting = BackupEncryption.Decrypt(new MemoryStream(encrypted), passphrase);
        var copy = new MemoryStream();
        decrypting.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(5 * Chunk + 17)]
    public void Content_of_every_size_comes_back(int size)
    {
        byte[] plain = RandomNumberGenerator.GetBytes(size);
        byte[] encrypted = Encrypt(plain);

        Assert.True(BackupEncryption.LooksEncrypted(encrypted));
        Assert.Equal(plain, ReadAll(encrypted));
    }

    [Fact]
    public void The_file_is_not_readable_without_the_passphrase()
    {
        byte[] plain = System.Text.Encoding.UTF8.GetBytes(new string('A', 5000));
        byte[] encrypted = Encrypt(plain);

        Assert.False(BackupEncryption.LooksEncrypted(plain));
        Assert.DoesNotContain("AAAAAAAA", System.Text.Encoding.UTF8.GetString(encrypted));
        Assert.Throws<BackupPassphraseException>(() => ReadAll(encrypted, "wrong"));
        Assert.Throws<BackupPassphraseException>(() => ReadAll(encrypted, string.Empty));
    }

    [Fact]
    public void Two_backups_of_the_same_content_look_different()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(3000);

        Assert.NotEqual(Encrypt(plain), Encrypt(plain));
    }

    [Fact]
    public void A_changed_byte_is_noticed_when_the_chunk_is_read()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(5 * Chunk);
        byte[] encrypted = Encrypt(plain);
        encrypted[BackupEncryption.HeaderBytes + Chunk / 2 + 2 * (Chunk + 21)] ^= 0x40;   // inside the third chunk

        Assert.Throws<BackupCorruptException>(() => ReadAll(encrypted));
    }

    [Fact]
    public void A_backup_that_is_cut_off_is_noticed_when_it_is_opened()
    {
        byte[] encrypted = Encrypt(RandomNumberGenerator.GetBytes(4 * Chunk + 100));

        Assert.Throws<BackupCorruptException>(() => ReadAll(encrypted[..^10]));
        Assert.Throws<BackupCorruptException>(() => ReadAll(encrypted[..BackupEncryption.HeaderBytes]));
        Assert.Throws<BackupCorruptException>(() => ReadAll(encrypted[..(BackupEncryption.HeaderBytes + (Chunk + 21) * 2)]));   // cut between two chunks
    }

    [Fact]
    public void Something_appended_is_noticed()
    {
        byte[] encrypted = Encrypt(RandomNumberGenerator.GetBytes(2 * Chunk));

        Assert.Throws<BackupCorruptException>(() => ReadAll([.. encrypted, .. new byte[40]]));
    }

    [Fact]
    public void Chunks_cannot_be_swapped()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(4 * Chunk);
        byte[] encrypted = Encrypt(plain);
        int record = Chunk + 21;
        byte[] swapped = (byte[])encrypted.Clone();
        Array.Copy(encrypted, BackupEncryption.HeaderBytes, swapped, BackupEncryption.HeaderBytes + record, record);
        Array.Copy(encrypted, BackupEncryption.HeaderBytes + record, swapped, BackupEncryption.HeaderBytes, record);

        Assert.Throws<BackupCorruptException>(() => ReadAll(swapped));
    }

    [Fact]
    public void A_damaged_header_is_noticed()
    {
        byte[] encrypted = Encrypt(RandomNumberGenerator.GetBytes(2 * Chunk));
        encrypted[28] ^= 1;   // the nonce prefix: the passphrase is still right, but nothing authenticates

        Assert.Throws<BackupCorruptException>(() => ReadAll(encrypted));
    }

    [Fact]
    public void The_content_can_be_read_with_random_access()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(10 * Chunk + 333);
        using Stream decrypting = BackupEncryption.Decrypt(new MemoryStream(Encrypt(plain)), "correct horse");

        Assert.Equal(plain.Length, decrypting.Length);
        Assert.True(decrypting.CanSeek);

        foreach (int offset in new[] { 7 * Chunk + 5, 0, plain.Length - 20, 3 * Chunk - 1, 3 * Chunk, 5 })
        {
            decrypting.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[Math.Min(600, plain.Length - offset)];
            decrypting.ReadExactly(buffer);
            Assert.Equal(plain[offset..(offset + buffer.Length)], buffer);
        }

        decrypting.Seek(-4, SeekOrigin.End);
        Assert.Equal(4, decrypting.Read(new byte[100]));
        Assert.Equal(0, decrypting.Read(new byte[100]));
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused()
    {
        byte[] encrypted = Encrypt(RandomNumberGenerator.GetBytes(100));

        Assert.Throws<ArgumentException>(() => BackupEncryption.Decrypt(new NonSeekableStream(encrypted), "correct horse"));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public class VersioningTests
{
    private static DataMigration Step(int version, List<int>? log = null)
        => new(version, $"step {version}", _ =>
        {
            log?.Add(version);
            return Task.CompletedTask;
        });

    [Fact]
    public void The_first_layout_has_number_one_and_no_steps()
    {
        Assert.Equal(1, DataMigrations.Latest);
        Assert.Empty(DataMigrations.All);
        Assert.Empty(DataMigrations.Pending(1));
    }

    [Fact]
    public void Steps_after_the_version_of_the_data_are_due_in_order()
    {
        IReadOnlyList<DataMigration> steps = [Step(2), Step(3), Step(4)];

        Assert.Equal(4, DataMigrations.LatestOf(steps));
        Assert.Equal([2, 3, 4], DataMigrations.Pending(1, steps).Select(s => s.Version));
        Assert.Equal([4], DataMigrations.Pending(3, steps).Select(s => s.Version));
        Assert.Empty(DataMigrations.Pending(4, steps));
    }

    [Fact]
    public void A_list_with_a_gap_or_a_wrong_start_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => DataMigrations.Pending(1, [Step(2), Step(4)]));
        Assert.Throws<InvalidOperationException>(() => DataMigrations.Pending(1, [Step(3)]));
        Assert.Throws<InvalidOperationException>(() => DataMigrations.Pending(1, [Step(2), Step(2)]));
    }

    [Fact]
    public async Task Running_the_steps_goes_from_the_old_version_to_the_latest()
    {
        var log = new List<int>();
        var context = new DataMigrationContext { DataDir = "unused", Connection = null!, Logger = NullLogger.Instance };

        int version = await DataMigrations.RunAsync(2, context, [Step(2, log), Step(3, log), Step(4, log)]);

        Assert.Equal(4, version);
        Assert.Equal([3, 4], log);
        Assert.Equal(1, await DataMigrations.RunAsync(1, context, []));
    }

    [Fact]
    public void A_database_of_a_newer_version_is_not_touched()
    {
        string[] known = ["20260101_A", "20260102_B"];

        VersionGuard.EnsureNotNewer(["20260101_A"], known, 1, 1);
        VersionGuard.EnsureNotNewer([], known, 1, 1);

        var newer = Assert.Throws<IncompatibleVersionException>(() => VersionGuard.EnsureNotNewer(["20260101_A", "20260102_B", "20260103_C"], known, 1, 1));
        Assert.Contains("20260103_C", newer.Message);
        Assert.Throws<IncompatibleVersionException>(() => VersionGuard.EnsureNotNewer(known, known, 3, 2));
    }

    [Fact]
    public void A_backup_is_restorable_when_this_version_knows_all_of_it()
    {
        string[] known = ["20260101_A", "20260102_B"];

        Assert.Null(VersionGuard.WhyNotRestorable("20260101_A", 1, 1, 1, known, 2));
        Assert.Null(VersionGuard.WhyNotRestorable("20260102_B", 2, 1, 3, known, 2));
        Assert.Contains("newer", VersionGuard.WhyNotRestorable("20260102_B", 1, 4, 1, known, 1));
        Assert.Contains("20260109_X", VersionGuard.WhyNotRestorable("20260109_X", 1, 1, 1, known, 1));
        Assert.Contains("layout 3", VersionGuard.WhyNotRestorable("20260102_B", 3, 1, 1, known, 2));
    }
}

using System.IO.Compression;
using System.Text.Json;

namespace MatMail.Backup;

/// <summary>What a person wants to know about a backup before restoring it.</summary>
public sealed record BackupInfo(
    DateTime CreatedUtc,
    string AppVersion,
    string? InstallationId,
    string? Host,
    bool Encrypted,
    long FileBytes,
    int Format,
    string? SchemaVersion,
    int DataVersion,
    int Tables,
    long Rows,
    int Files,
    long FilesBytes);

/// <summary>An opened backup file (plain or encrypted): its manifest and the content of its parts, each checked against the manifest while it is read.</summary>
public sealed class BackupArchive : IDisposable
{
    private const long MaxManifestBytes = 256L * 1024 * 1024;

    private readonly FileStream _file;
    private readonly Stream _plain;
    private readonly ZipArchive _zip;

    private BackupArchive(FileStream file, Stream plain, ZipArchive zip, BackupManifest manifest, bool encrypted)
    {
        _file = file;
        _plain = plain;
        _zip = zip;
        Manifest = manifest;
        Encrypted = encrypted;
    }

    public BackupManifest Manifest { get; }
    public bool Encrypted { get; }
    public long FileBytes => _file.Length;

    /// <summary>Opens a backup. An encrypted one needs its passphrase (<see cref="BackupPassphraseException"/> when it is missing or wrong).</summary>
    /// <exception cref="BackupCorruptException">It is no backup, or damaged.</exception>
    public static BackupArchive Open(string path, string? passphrase = null)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        Stream? plain = null;
        ZipArchive? zip = null;
        try
        {
            byte[] start = new byte[BackupEncryption.HeaderBytes];
            int read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            bool encrypted = BackupEncryption.LooksEncrypted(start.AsSpan(0, read));
            file.Position = 0;

            plain = encrypted ? BackupEncryption.Decrypt(file, passphrase ?? string.Empty) : file;
            try
            {
                zip = new ZipArchive(plain, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException ex)
            {
                throw new BackupCorruptException("This is not a MatMail backup (no zip archive), or it is damaged.", ex);
            }

            BackupManifest manifest = ReadManifest(zip);
            return new BackupArchive(file, plain, zip, manifest, encrypted);
        }
        catch
        {
            zip?.Dispose();
            if (!ReferenceEquals(plain, file))
            {
                plain?.Dispose();
            }

            file.Dispose();
            throw;
        }
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        ZipArchiveEntry entry = zip.GetEntry(BackupFormat.ManifestEntry)
            ?? throw new BackupCorruptException("The archive has no manifest.json: it is not a MatMail backup, or it is cut off.");
        if (entry.Length > MaxManifestBytes)
        {
            throw new BackupCorruptException("The manifest of the backup is implausibly large.");
        }

        try
        {
            using Stream stream = entry.Open();
            BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(stream, BackupFormat.Json)
                ?? throw new BackupCorruptException("The manifest of the backup is empty.");
            if (manifest.Format < 1)
            {
                throw new BackupCorruptException("The manifest of the backup has no valid format number.");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new BackupCorruptException("The manifest of the backup cannot be read.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new BackupCorruptException("The manifest of the backup is damaged.", ex);
        }
    }

    public BackupInfo Describe() => new(
        Manifest.CreatedUtc,
        Manifest.AppVersion,
        Manifest.InstallationId,
        Manifest.Host,
        Encrypted,
        FileBytes,
        Manifest.Format,
        Manifest.Database.SchemaVersion,
        Manifest.Database.DataVersion,
        Manifest.Database.Tables.Count,
        Manifest.Database.Tables.Sum(t => t.Rows),
        Manifest.Files.Count,
        Manifest.Files.Sum(f => f.Bytes));

    /// <summary>The content of an entry; reading it to the end checks size and checksum and fails when the backup is damaged.</summary>
    public Stream OpenVerified(string entryName, long bytes, string sha256)
    {
        ZipArchiveEntry entry = _zip.GetEntry(entryName)
            ?? throw new BackupCorruptException($"“{entryName}” is missing in the backup: it is cut off or incomplete.");
        try
        {
            return new VerifyingReadStream(entry.Open(), entryName, bytes, sha256);
        }
        catch (InvalidDataException ex)
        {
            throw new BackupCorruptException($"“{entryName}” cannot be read: the backup is damaged.", ex);
        }
    }

    public Stream OpenPart(ManifestPart part) => OpenVerified(part.Entry, part.Bytes, part.Sha256);

    public Stream OpenFile(ManifestFile file) => OpenVerified(BackupFormat.DataPrefix + file.Path, file.Bytes, file.Sha256);

    /// <summary>The whole table as one stream (its parts one after the other).</summary>
    public Stream OpenTable(ManifestTable table) => new ConcatenatedReadStream(table.Parts.Select(p => (Func<Stream>)(() => OpenPart(p))));

    /// <summary>Reads every part and every file and compares size and checksum with the manifest: a backup that passes can be restored.</summary>
    public async Task VerifyAsync(IProgress<BackupProgress>? progress = null, CancellationToken cancel = default)
    {
        int total = Manifest.Database.Tables.Sum(t => t.Parts.Count) + Manifest.Files.Count;
        int done = 0;
        long bytes = 0;

        foreach (ManifestTable table in Manifest.Database.Tables)
        {
            foreach (ManifestPart part in table.Parts)
            {
                progress?.Report(new BackupProgress("verify", table.Name, done++, total, bytes));
                bytes += await ReadThroughAsync(OpenPart(part), cancel);
            }
        }

        foreach (ManifestFile file in Manifest.Files)
        {
            progress?.Report(new BackupProgress("verify", file.Path, done++, total, bytes));
            bytes += await ReadThroughAsync(OpenFile(file), cancel);
        }

        progress?.Report(new BackupProgress("done", null, total, total, bytes));
    }

    private static async Task<long> ReadThroughAsync(Stream stream, CancellationToken cancel)
    {
        await using (stream)
        {
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                }
            }
            catch (InvalidDataException ex)
            {
                throw new BackupCorruptException("A part of the backup cannot be read: it is damaged.", ex);
            }

            return total;
        }
    }

    public void Dispose()
    {
        _zip.Dispose();
        if (!ReferenceEquals(_plain, _file))
        {
            _plain.Dispose();
        }

        _file.Dispose();
    }
}

/// <summary>The parts of the name of a backup file (see <see cref="BackupFiles.Name"/>).</summary>
public sealed record BackupFileName(string Installation, string Label, DateTime CreatedUtc, string Version, bool Encrypted)
{
    /// <summary>Made by a schedule (its label is p followed by the id of the plan).</summary>
    public bool IsPlan => Label.Length > 1 && Label[0] == 'p' && char.IsAsciiDigit(Label[1]);
}

/// <summary>Names and conversions of backup files.</summary>
public static partial class BackupFiles
{
    public const string PlainExtension = ".zip";
    public const string EncryptedExtension = ".mmbak";

    public const string ManualLabel = "manual";
    public const string PreRestoreLabel = "pre-restore";

    [System.Text.RegularExpressions.GeneratedRegex(@"^matmail-(?<inst>[0-9a-f]{6})-(?<label>p[0-9]+|manual|pre-restore)-(?<ts>[0-9]{8}-[0-9]{6})-(?<ver>[A-Za-z0-9.\-]+)\.(?<ext>zip|mmbak)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NamePattern();

    /// <summary>
    /// The name of a backup file: <c>matmail-3f9a1c-p2-20261008-031500-0.1.42-20261007.zip</c> = installation (the first six characters of its
    /// id, so that installations sharing a folder never take each other's files for their own), what made it (<c>p2</c> = plan 2,
    /// <c>manual</c>, <c>pre-restore</c>), UTC time, version of the program; <c>.mmbak</c> when encrypted.
    /// </summary>
    public static string Name(DateTime createdUtc, string appVersion, bool encrypted, string label = ManualLabel, string? installationId = null)
    {
        string version = new(appVersion.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray());
        return $"matmail-{Short(installationId)}-{label}-{createdUtc:yyyyMMdd-HHmmss}-{version}{(encrypted ? EncryptedExtension : PlainExtension)}";
    }

    /// <summary>The first six characters of an installation id (lower case, hex), or zeros when there is none yet.</summary>
    public static string Short(string? installationId)
    {
        string digits = new((installationId ?? string.Empty).Where(char.IsAsciiHexDigit).Select(char.ToLowerInvariant).Take(6).ToArray());
        return digits.PadRight(6, '0');
    }

    public static BackupFileName? Parse(string name)
    {
        System.Text.RegularExpressions.Match match = NamePattern().Match(name);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["ts"].Value, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime created))
        {
            return null;
        }

        return new BackupFileName(match.Groups["inst"].Value, match.Groups["label"].Value, created, match.Groups["ver"].Value, match.Groups["ext"].Value == "mmbak");
    }

    public static bool IsBackupFileName(string name) => Parse(name) is not null;

    /// <summary>Encrypts a finished archive into a new file.</summary>
    public static async Task EncryptAsync(string plainPath, string encryptedPath, string passphrase, CancellationToken cancel = default)
    {
        await using var input = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await using (Stream encrypting = BackupEncryption.Encrypt(output, passphrase))
        {
            await input.CopyToAsync(encrypting, 128 * 1024, cancel);
        }

        await output.FlushAsync(cancel);
    }

    /// <summary>Whether a file is an encrypted backup (by its first bytes, not by its name).</summary>
    public static bool IsEncrypted(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] start = new byte[4];
        int read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        return BackupEncryption.LooksEncrypted(start.AsSpan(0, read));
    }
}

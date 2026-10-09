using System.Data;
using System.IO.Compression;
using System.Text.Json;
using MatMail.Versioning;
using Npgsql;

namespace MatMail.Backup;

/// <summary>What a backup is taken of: the PostgreSQL database and the data volume.</summary>
public sealed record BackupSource(string ConnectionString, string DataDir)
{
    /// <summary>More folders (absolute paths) that are left out besides <see cref="BackupFormat.ExcludedTopLevel"/>, e.g. a backup folder somewhere else in the data volume.</summary>
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];

    /// <summary>A table is cut into parts of about this many bytes (uncompressed); only tests make it small.</summary>
    public long PartBytes { get; init; } = BackupFormat.PartBytes;
}

/// <summary>
/// Writes a complete backup as one zip archive: every table of the database as a PostgreSQL binary COPY (in one consistent snapshot),
/// every file of the data volume, and last the manifest with sizes and checksums of all of it. Nothing is listed by hand: the tables
/// are the tables the database has, the files are the files the volume has, so what future versions add is backed up without a change here.
/// </summary>
public static class BackupWriter
{
    private const int CopyBufferBytes = 128 * 1024;
    private const int MaxDirectoryDepth = 40;

    /// <summary>Writes the archive to <paramref name="output"/> (a seekable stream, usually a file); an encrypted backup is made by <see cref="BackupFiles.EncryptAsync"/> afterwards.</summary>
    public static async Task<BackupManifest> WriteAsync(BackupSource source, Stream output, IProgress<BackupProgress>? progress = null, CancellationToken cancel = default)
    {
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("The archive is written to a file: the stream must be seekable.", nameof(output));
        }

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            AppVersion = AppInfo.Version,
            Host = Environment.MachineName,
        };
        manifest.Notes.Add("Not part of a backup: the folders " + string.Join(" and ", BackupFormat.ExcludedTopLevel.Select(f => $"“{f}”")) + " of the data volume (scratch space and the backups themselves).");

        var run = new Run(progress, cancel);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteDatabaseAsync(source, zip, manifest, run);
            await WriteFilesAsync(source, zip, manifest, run);

            ZipArchiveEntry entry = zip.CreateEntry(BackupFormat.ManifestEntry, CompressionLevel.Optimal);
            await using Stream stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, manifest, BackupFormat.Json, cancel);
        }

        await output.FlushAsync(cancel);
        run.Report("done", null, 1, 1);
        return manifest;
    }

    private static async Task WriteDatabaseAsync(BackupSource source, ZipArchive zip, BackupManifest manifest, Run run)
    {
        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString) { CommandTimeout = 0, Pooling = false, TcpKeepAlive = true };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(run.Cancel);

        // One snapshot for the whole export: all tables are of the same moment, whatever the application does meanwhile.
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, run.Cancel);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", run.Cancel);

        ManifestDatabase database = manifest.Database;
        database.ServerVersion = connection.PostgreSqlVersion.ToString();

        if (await ExistsAsync(connection, transaction, "__EFMigrationsHistory", run.Cancel))
        {
            database.AppliedMigrations = await ReadStringsAsync(connection, transaction, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", run.Cancel);
            database.SchemaVersion = database.AppliedMigrations.LastOrDefault();
        }

        if (await ExistsAsync(connection, transaction, "SystemSetting", run.Cancel))
        {
            manifest.InstallationId = await ScalarStringAsync(connection, transaction, $"SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = '{SystemSettings.InstallationId}'", run.Cancel);
            string? dataVersion = await ScalarStringAsync(connection, transaction, $"SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = '{SystemSettings.DataVersion}'", run.Cancel);
            database.DataVersion = int.TryParse(dataVersion, out int parsed) && parsed > 0 ? parsed : 1;
        }

        List<string> tables = await ReadStringsAsync(
            connection,
            transaction,
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
            ORDER BY c.relname
            """,
            run.Cancel);

        for (int i = 0; i < tables.Count; i++)
        {
            run.Report("database", tables[i], i, tables.Count);
            database.Tables.Add(await WriteTableAsync(connection, transaction, zip, tables[i], source.PartBytes, run));
        }

        await using (var sequences = new NpgsqlCommand("SELECT sequencename, last_value FROM pg_sequences WHERE schemaname = 'public' ORDER BY sequencename", connection, transaction))
        await using (NpgsqlDataReader reader = await sequences.ExecuteReaderAsync(run.Cancel))
        {
            while (await reader.ReadAsync(run.Cancel))
            {
                database.Sequences.Add(new ManifestSequence { Name = reader.GetString(0), LastValue = reader.IsDBNull(1) ? null : reader.GetInt64(1) });
            }
        }

        await transaction.CommitAsync(run.Cancel);
    }

    private static async Task<ManifestTable> WriteTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ZipArchive zip, string table, long partBytes, Run run)
    {
        // Generated columns are computed by the database and cannot be loaded; everything else is a column of the data.
        List<string> columns = await ReadStringsAsync(
            connection,
            transaction,
            $"""
            SELECT a.attname FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = '{table.Replace("'", "''")}' AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
            ORDER BY a.attnum
            """,
            run.Cancel);

        var result = new ManifestTable
        {
            Name = table,
            Columns = columns,
            Rows = Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT count(*) FROM {Quote(table)}", run.Cancel), System.Globalization.CultureInfo.InvariantCulture),
        };

        string sql = $"COPY {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) TO STDOUT (FORMAT BINARY)";
        await using NpgsqlRawCopyStream copy = await connection.BeginRawBinaryCopyAsync(sql, run.Cancel);

        byte[] buffer = new byte[CopyBufferBytes];
        Part? part = null;
        try
        {
            while (true)
            {
                int read = await copy.ReadAsync(buffer, run.Cancel);
                if (read == 0)
                {
                    break;
                }

                part ??= Part.Open(zip, $"{BackupFormat.DatabasePrefix}{table}.{result.Parts.Count + 1:D4}.copy");
                await part.WriteAsync(buffer.AsMemory(0, read), run.Cancel);
                run.Bytes += read;

                // The raw stream is cut at any byte: the restore reads the parts one after the other as one stream.
                if (part.Bytes >= partBytes)
                {
                    result.Parts.Add(part.Complete());
                    part = null;
                }
            }

            if (part is not null)
            {
                result.Parts.Add(part.Complete());
                part = null;
            }
        }
        finally
        {
            part?.Dispose();
        }

        return result;
    }

    private static async Task WriteFilesAsync(BackupSource source, ZipArchive zip, BackupManifest manifest, Run run)
    {
        string root = Path.GetFullPath(source.DataDir);
        if (!Directory.Exists(root))
        {
            return;
        }

        List<string> files = ListFiles(root, source.ExcludedDirectories.Select(Path.GetFullPath).ToList());
        for (int i = 0; i < files.Count; i++)
        {
            run.Cancel.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            run.Report("files", relative, i, files.Count);

            // A file that is replaced while it is read is read as it is (the checksum is taken of what goes into the archive);
            // one that is gone by now is not part of the volume any more.
            FileStream input;
            try
            {
                input = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            await using (input)
            {
                DateTime modified = File.GetLastWriteTimeUtc(input.SafeFileHandle);
                ZipArchiveEntry entry = zip.CreateEntry(BackupFormat.DataPrefix + relative, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(modified < ZipEpoch ? ZipEpoch : modified, TimeSpan.Zero);

                long bytes;
                string sha;
                await using (Stream stream = entry.Open())
                using (var hashing = new HashingWriteStream(stream))
                {
                    await input.CopyToAsync(hashing, CopyBufferBytes, run.Cancel);
                    bytes = hashing.BytesWritten;
                    sha = hashing.Sha256Hex;
                }

                run.Bytes += bytes;
                manifest.Files.Add(new ManifestFile { Path = relative, Bytes = bytes, Sha256 = sha, ModifiedUtc = modified });
            }
        }
    }

    private static readonly DateTime ZipEpoch = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>All files of the data volume except the excluded places, in a fixed order.</summary>
    private static List<string> ListFiles(string root, IReadOnlyList<string> excludedDirectories)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        bool Excluded(string path, bool topLevel)
        {
            if (topLevel && BackupFormat.ExcludedTopLevel.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            {
                return true;
            }

            return excludedDirectories.Any(e => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), e.TrimEnd(Path.DirectorySeparatorChar), comparison));
        }

        var files = new List<string>();
        void Walk(string directory, int depth)
        {
            if (depth > MaxDirectoryDepth)
            {
                throw new InvalidOperationException($"The data volume is nested deeper than {MaxDirectoryDepth} folders at {directory} (a link that loops?).");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    if (!Excluded(entry, depth == 0))
                    {
                        Walk(entry, depth + 1);
                    }
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        Walk(root, 0);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancel)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancel);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancel)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        object? value = await command.ExecuteScalarAsync(cancel);
        return value is DBNull ? null : value;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancel)
        => (await ScalarAsync(connection, transaction, sql, cancel)) as string;

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancel)
        => await ScalarAsync(connection, transaction, $"SELECT to_regclass('public.{Quote(table)}') IS NOT NULL", cancel) is true;

    private static async Task<List<string>> ReadStringsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancel)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancel);
        while (await reader.ReadAsync(cancel))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>The state of one run: progress, the bytes so far, cancellation.</summary>
    private sealed class Run(IProgress<BackupProgress>? progress, CancellationToken cancel)
    {
        public CancellationToken Cancel { get; } = cancel;
        public long Bytes { get; set; }

        public void Report(string stage, string? item, int done, int total) => progress?.Report(new BackupProgress(stage, item, done, total, Bytes));
    }

    /// <summary>An entry of the archive that is being written: counts and checksums what goes in.</summary>
    private sealed class Part : IDisposable
    {
        private readonly string _name;
        private readonly Stream _entry;
        private readonly HashingWriteStream _hashing;

        private Part(string name, Stream entry)
        {
            _name = name;
            _entry = entry;
            _hashing = new HashingWriteStream(entry);
        }

        public long Bytes => _hashing.BytesWritten;

        public static Part Open(ZipArchive zip, string name) => new(name, zip.CreateEntry(name, CompressionLevel.Fastest).Open());

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel) => _hashing.WriteAsync(data, cancel);

        public ManifestPart Complete()
        {
            var result = new ManifestPart { Entry = _name, Bytes = _hashing.BytesWritten, Sha256 = _hashing.Sha256Hex };
            Dispose();
            return result;
        }

        public void Dispose()
        {
            _hashing.Dispose();
            _entry.Dispose();
        }
    }
}

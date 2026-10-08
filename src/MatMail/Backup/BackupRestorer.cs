using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatMail.Data;
using MatMail.Services;
using MatMail.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MatMail.Backup;

public sealed class RestoreOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>The data volume the files are restored to.</summary>
    public required string DataDir { get; init; }

    /// <summary>Where the files are laid out and checked before they are put in place (default: <c>tmp</c> in the data volume, which is on the same volume, so putting them in place is a rename).</summary>
    public string? StagingDirectory { get; init; }

    /// <summary>The steps of <see cref="DataMigrations"/> to run (null = the program's own; tests bring their own).</summary>
    public IReadOnlyList<DataMigration>? DataMigrationSteps { get; init; }

    /// <summary>
    /// Outgoing mail that was still queued when the backup was made may have been sent in the meantime; sending it again would be a
    /// duplicate to the recipient. So it is put on "failed" with a note instead, and the administrator retries what is really missing.
    /// </summary>
    public bool HoldOutboundQueue { get; init; } = true;
}

public sealed record RestoreResult(
    int Tables,
    long Rows,
    int Files,
    string FromSchema,
    string ToSchema,
    int FromDataVersion,
    int ToDataVersion,
    DateTime BackupCreatedUtc,
    string BackupAppVersion,
    TimeSpan Duration);

/// <summary>
/// Puts a backup in place of what is there: the database is rebuilt in the schema version of the backup, loaded, and migrated forward
/// to this program's version (exactly like an upgrade would), the files are brought to the layout of this version, and both replace
/// the current ones. All of it is one database transaction: a backup that is damaged, from a newer version or does not fit stops the
/// restore and leaves everything as it was.
/// </summary>
public static class BackupRestorer
{
    public static Task<RestoreResult> RestoreAsync(BackupArchive archive, RestoreOptions options, ILogger logger, IProgress<BackupProgress>? progress = null, CancellationToken cancel = default)
        => new RestoreRun(archive, options, logger, progress, cancel).RunAsync();

    private sealed class RestoreRun
    {
        private const int CopyBufferBytes = 128 * 1024;

        private static readonly string[] DeploymentSections = ["Database", "Server"];

        private readonly BackupArchive _archive;
        private readonly BackupManifest _manifest;
        private readonly RestoreOptions _options;
        private readonly ILogger _logger;
        private readonly IProgress<BackupProgress>? _progress;
        private readonly CancellationToken _cancel;
        private readonly IReadOnlyList<DataMigration> _steps;
        private readonly string _dataDir;
        private readonly string _staging;
        private readonly string _stagedFiles;
        private readonly string _previous;

        private NpgsqlConnection _connection = null!;
        private NpgsqlTransaction _transaction = null!;
        private long _bytes;
        private bool _keepStaging;

        public RestoreRun(BackupArchive archive, RestoreOptions options, ILogger logger, IProgress<BackupProgress>? progress, CancellationToken cancel)
        {
            _archive = archive;
            _manifest = archive.Manifest;
            _options = options;
            _logger = logger;
            _progress = progress;
            _cancel = cancel;
            _steps = options.DataMigrationSteps ?? DataMigrations.All;
            _dataDir = Path.GetFullPath(options.DataDir);
            _staging = Path.Combine(Path.GetFullPath(options.StagingDirectory ?? Path.Combine(_dataDir, "tmp")), "restore-" + Guid.NewGuid().ToString("N")[..10]);
            _stagedFiles = Path.Combine(_staging, "files");
            _previous = Path.Combine(_staging, "previous");
        }

        public async Task<RestoreResult> RunAsync()
        {
            DateTime started = DateTime.UtcNow;
            Check();

            Directory.CreateDirectory(_stagedFiles);
            try
            {
                await StageFilesAsync();
                KeepDeploymentSettings();
                return await RestoreDatabaseAndSwapAsync(started);
            }
            catch (InvalidDataException ex)
            {
                // the zip or the compression of a part is damaged
                throw new BackupCorruptException("The backup is damaged: " + ex.Message, ex);
            }
            finally
            {
                // What the old files were moved to stays when putting them back failed: it is all that is left of them.
                if (!_keepStaging)
                {
                    TryDelete(_staging);
                }
            }
        }

        /// <summary>Everything that can be known before anything is touched.</summary>
        private void Check()
        {
            ManifestDatabase database = _manifest.Database;
            if (!string.Equals(database.Provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupIncompatibleException($"The backup holds a {database.Provider} database; MatMail uses PostgreSQL.");
            }

            if (database.SchemaVersion is null)
            {
                throw new BackupIncompatibleException("The backup has no database (it does not say which schema version it has).");
            }

            using MatMailDbContext schema = CreateSchemaContext(_options.ConnectionString);
            string? why = VersionGuard.WhyNotRestorable(
                database.SchemaVersion,
                database.DataVersion,
                _manifest.Format,
                BackupFormat.Version,
                schema.Database.GetMigrations(),
                DataMigrations.LatestOf(_steps));
            if (why is not null)
            {
                throw new BackupIncompatibleException(why);
            }

            // A list of steps with a gap would only show when it is needed: better now.
            DataMigrations.Pending(database.DataVersion, _steps);
        }

        private static MatMailDbContext CreateSchemaContext(string connectionString)
            => new(new DbContextOptionsBuilder<MatMailDbContext>().UseNpgsql(connectionString).Options, new CurrentUser());

        /// <summary>A context on the connection of the restore, to apply migrations in its transaction (EF Core warns about that, as it takes its own lock otherwise; nobody else works on the database now).</summary>
        private static MatMailDbContext CreateMigrationContext(NpgsqlConnection connection)
            => new(
                new DbContextOptionsBuilder<MatMailDbContext>()
                    .UseNpgsql(connection)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.MigrationsUserTransactionWarning))
                    .Options,
                new CurrentUser());

        // -----------------------------------------------------------------------------------------
        // Files: laid out next to the data volume first, every byte checked against the manifest
        // -----------------------------------------------------------------------------------------

        private async Task StageFilesAsync()
        {
            string root = _stagedFiles + Path.DirectorySeparatorChar;
            int done = 0;
            foreach (ManifestFile file in _manifest.Files)
            {
                _cancel.ThrowIfCancellationRequested();
                Report("files", file.Path, done++, _manifest.Files.Count);

                string[] segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    throw new BackupCorruptException("The backup lists a file without a name.");
                }

                // Scratch space and the backups themselves are never part of the data that is replaced.
                if (BackupFormat.ExcludedTopLevel.Contains(segments[0], StringComparer.Ordinal))
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(_stagedFiles, string.Join(Path.DirectorySeparatorChar, segments)));
                if (!target.StartsWith(root, StringComparison.Ordinal) || segments.Contains(".."))
                {
                    throw new BackupCorruptException($"“{file.Path}” in the backup points outside the data volume.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (Stream source = _archive.OpenFile(file))
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
                {
                    await source.CopyToAsync(output, CopyBufferBytes, _cancel);
                }

                _bytes += file.Bytes;
                try
                {
                    File.SetLastWriteTimeUtc(target, file.ModifiedUtc);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // a date the file system cannot hold: the file keeps the time of the restore
                }
            }
        }

        /// <summary>
        /// How this server is deployed (the connection to its database, its ports and HTTPS) is not what the backup says but what the
        /// machine it is restored to needs: a restore must never make the server unreachable. Everything else in the settings file comes from the backup.
        /// </summary>
        private void KeepDeploymentSettings()
        {
            string live = Path.Combine(_dataDir, "config", "app.json");
            string staged = Path.Combine(_stagedFiles, "config", "app.json");
            if (!Directory.Exists(Path.Combine(_stagedFiles, "config")))
            {
                return;   // the backup does not replace the settings
            }

            JsonObject? liveSettings = ReadObject(live);
            JsonObject? stagedSettings = ReadObject(staged);
            if (stagedSettings is null)
            {
                if (liveSettings is not null)
                {
                    File.Copy(live, staged, overwrite: true);
                    _logger.LogWarning("The backup has no usable config/app.json; the current one stays.");
                }

                return;
            }

            foreach (string section in DeploymentSections)
            {
                foreach (string key in stagedSettings.Select(p => p.Key).Where(k => string.Equals(k, section, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    stagedSettings.Remove(key);
                }

                KeyValuePair<string, JsonNode?> current = liveSettings?.FirstOrDefault(p => string.Equals(p.Key, section, StringComparison.OrdinalIgnoreCase)) ?? default;
                if (current.Value is not null)
                {
                    stagedSettings[current.Key] = current.Value.DeepClone();
                }
            }

            File.WriteAllText(staged, stagedSettings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static JsonObject? ReadObject(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------------------------
        // Database: one transaction from the empty schema to the current version
        // -----------------------------------------------------------------------------------------

        private async Task<RestoreResult> RestoreDatabaseAndSwapAsync(DateTime started)
        {
            var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString) { CommandTimeout = 0, Pooling = false, TcpKeepAlive = true };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(_cancel);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(_cancel);
            _connection = connection;
            _transaction = transaction;

            // A session of someone else that holds a table must not make the restore wait for ever.
            await ExecuteAsync("SET LOCAL lock_timeout = '60s'");

            Report("schema", null, 0, 1);
            await ExecuteAsync("DROP SCHEMA IF EXISTS public CASCADE");
            await ExecuteAsync("CREATE SCHEMA public");

            // The schema as it was when the backup was made: the data fits it exactly. The migrations are applied by EF Core itself (a
            // script of them is not the same: the SQL that migrations carry is only separated from the next command by EF's own
            // bookkeeping), inside the transaction of the restore.
            await using MatMailDbContext migrations = CreateMigrationContext(connection);
            await migrations.Database.UseTransactionAsync(transaction, _cancel);
            IMigrator migrator = migrations.Database.GetService<IMigrator>();

            // EF Core reads the history of applied migrations and takes a missing table as "none yet" - a failed query that would end a
            // transaction, so the (empty) table is there from the start.
            await ExecuteAsync(migrations.Database.GetService<IHistoryRepository>().GetCreateIfNotExistsScript());
            string fromSchema = _manifest.Database.SchemaVersion!;
            string toSchema = migrations.Database.GetMigrations().Last();
            await migrator.MigrateAsync(fromSchema, _cancel);

            // Rows that a migration may have put in (seed data) are part of the backup if they still exist: start empty.
            List<string> tables = await ReadStringsAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename");
            if (tables.Count > 0)
            {
                await ExecuteAsync("TRUNCATE " + string.Join(", ", tables.Select(Quote)));
            }

            // The tables are loaded one by one in any order: the keys that tie them together are taken off meanwhile and put back
            // (and checked) afterwards, so no order of tables has to be worked out, and no cycle between tables can get in the way.
            List<ForeignKey> foreignKeys = await DropForeignKeysAsync();
            long rows = await LoadTablesAsync(tables);
            await AddForeignKeysAsync(foreignKeys);
            await SetSequencesAsync();

            // The way a normal upgrade goes: the migrations after the backup's version, over the data that was just loaded.
            Report("migrate", toSchema, 0, 1);
            await migrator.MigrateAsync(null, _cancel);

            int fromDataVersion = _manifest.Database.DataVersion;
            int toDataVersion = await DataMigrations.RunAsync(
                fromDataVersion,
                new DataMigrationContext { DataDir = _stagedFiles, Connection = _connection, Transaction = _transaction, Logger = _logger },
                _steps);

            await SaveVersionsAsync(toDataVersion);
            await FixUpAsync();

            // Files in place, then the commit: if the commit does not happen, the old files are put back.
            Report("files", null, 0, 1);
            var swap = new FileSwap(_dataDir, _stagedFiles, _previous, _logger);
            try
            {
                swap.Apply();
                await transaction.CommitAsync(_cancel);
            }
            catch
            {
                swap.Revert();
                _keepStaging = swap.RevertFailed;
                throw;
            }

            Report("done", null, 1, 1);
            return new RestoreResult(
                _manifest.Database.Tables.Count,
                rows,
                _manifest.Files.Count,
                fromSchema,
                toSchema,
                fromDataVersion,
                toDataVersion,
                _manifest.CreatedUtc,
                _manifest.AppVersion,
                DateTime.UtcNow - started);
        }

        private async Task<long> LoadTablesAsync(List<string> existingTables)
        {
            HashSet<string> existing = existingTables.ToHashSet(StringComparer.Ordinal);
            long total = 0;
            int done = 0;
            foreach (ManifestTable table in _manifest.Database.Tables)
            {
                _cancel.ThrowIfCancellationRequested();
                Report("database", table.Name, done++, _manifest.Database.Tables.Count);
                if (!existing.Contains(table.Name))
                {
                    throw new BackupIncompatibleException($"The backup holds the table “{table.Name}”, which the database of schema version {_manifest.Database.SchemaVersion} does not have.");
                }

                if (table.Parts.Count == 0)
                {
                    continue;
                }

                await LoadTableAsync(table);
                long loaded = Convert.ToInt64(await ScalarAsync($"SELECT count(*) FROM {Quote(table.Name)}"), CultureInfo.InvariantCulture);
                if (loaded != table.Rows)
                {
                    throw new BackupCorruptException($"The table “{table.Name}” has {loaded} rows after loading, the backup says {table.Rows}: the backup is incomplete.");
                }

                total += loaded;
                _bytes += table.Parts.Sum(p => p.Bytes);
            }

            return total;
        }

        private async Task LoadTableAsync(ManifestTable table)
        {
            string columns = string.Join(", ", table.Columns.Select(Quote));
            await using Stream source = _archive.OpenTable(table);
            try
            {
                await using NpgsqlRawCopyStream target = await _connection.BeginRawBinaryCopyAsync($"COPY {Quote(table.Name)} ({columns}) FROM STDIN (FORMAT BINARY)", _cancel);
                try
                {
                    await source.CopyToAsync(target, CopyBufferBytes, _cancel);
                }
                catch
                {
                    // Nothing of a half-loaded table may stay: the copy is cancelled (the transaction is rolled back anyway).
                    try
                    {
                        target.Cancel();
                    }
                    catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
                    {
                        // the first error is the one that counts
                    }

                    throw;
                }
            }
            catch (PostgresException) when (!_cancel.IsCancellationRequested)
            {
                // The server may meet a damaged part before its checksum can say so; reading the rest of the part does. When it is intact
                // the error is the database's own (a value that does not fit) and stands.
                await source.CopyToAsync(Stream.Null, _cancel);
                throw;
            }
        }

        private sealed record ForeignKey(string Table, string Name, string Definition);

        private async Task<List<ForeignKey>> DropForeignKeysAsync()
        {
            var keys = new List<ForeignKey>();
            await using (var command = new NpgsqlCommand(
                """
                SELECT cl.relname, c.conname, pg_get_constraintdef(c.oid)
                FROM pg_constraint c JOIN pg_class cl ON cl.oid = c.conrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
                WHERE c.contype = 'f' AND n.nspname = 'public'
                ORDER BY cl.relname, c.conname
                """,
                _connection,
                _transaction))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(_cancel))
            {
                while (await reader.ReadAsync(_cancel))
                {
                    keys.Add(new ForeignKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            foreach (ForeignKey key in keys)
            {
                await ExecuteAsync($"ALTER TABLE {Quote(key.Table)} DROP CONSTRAINT {Quote(key.Name)}");
            }

            return keys;
        }

        private async Task AddForeignKeysAsync(List<ForeignKey> keys)
        {
            Report("constraints", null, 0, keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                _cancel.ThrowIfCancellationRequested();
                try
                {
                    await ExecuteAsync($"ALTER TABLE {Quote(keys[i].Table)} ADD CONSTRAINT {Quote(keys[i].Name)} {keys[i].Definition}");
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                {
                    throw new BackupCorruptException($"The data in the backup is inconsistent: {ex.MessageText} ({keys[i].Table}, {keys[i].Name}).", ex);
                }
            }
        }

        /// <summary>The counters that hand out new ids continue where they were, so no id is given twice.</summary>
        private async Task SetSequencesAsync()
        {
            foreach (ManifestSequence sequence in _manifest.Database.Sequences.Where(s => s.LastValue is > 0))
            {
                await using var command = new NpgsqlCommand("SELECT setval(to_regclass(@name), @value, true)", _connection, _transaction);
                command.Parameters.AddWithValue("name", "public." + Quote(sequence.Name));
                command.Parameters.AddWithValue("value", sequence.LastValue!.Value);
                await command.ExecuteScalarAsync(_cancel);   // a counter that the schema no longer has: to_regclass is null, setval does nothing
            }
        }

        private async Task SaveVersionsAsync(int dataVersion)
        {
            await SaveSettingAsync(SystemSettings.DataVersion, dataVersion.ToString(CultureInfo.InvariantCulture));
            await SaveSettingAsync(SystemSettings.AppVersion, AppInfo.Version);
            await SaveSettingAsync(
                SystemSettings.LastRestore,
                JsonSerializer.Serialize(new { restoredUtc = DateTime.UtcNow, backupCreatedUtc = _manifest.CreatedUtc, backupVersion = _manifest.AppVersion, backupInstallationId = _manifest.InstallationId }));
        }

        private async Task SaveSettingAsync(string key, string value)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO "SystemSetting" ("Key", "Value", "CreateDate", "UpdateDate") VALUES (@key, @value, now(), now())
                ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value", "UpdateDate" = now()
                """,
                _connection,
                _transaction);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync(_cancel);
        }

        /// <summary>What a restored state needs that its data cannot say itself.</summary>
        private async Task FixUpAsync()
        {
            // IMAP clients remember the numbers of the messages of a folder. The restored state may be older than what they have seen: new
            // messages would get numbers that meant other messages before. A new UIDVALIDITY makes every client load the folders again.
            await ExecuteAsync("UPDATE \"MailFolder\" SET \"UidValidity\" = GREATEST(\"UidValidity\" + 1, EXTRACT(EPOCH FROM now())::bigint)");

            if (_options.HoldOutboundQueue)
            {
                await using var command = new NpgsqlCommand(
                    """
                    UPDATE "OutboundMessage" SET "Status" = 'Failed', "UpdateDate" = now(),
                        "LastError" = 'Restored from a backup: not sent automatically. Check whether it was delivered, then retry it.'
                    WHERE "Status" IN ('Pending', 'Sending')
                    """,
                    _connection,
                    _transaction);
                int held = await command.ExecuteNonQueryAsync(_cancel);
                if (held > 0)
                {
                    _logger.LogWarning("{Count} queued outgoing messages were put on hold: they may have been sent after the backup was made.", held);
                }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Small helpers
        // -----------------------------------------------------------------------------------------

        private void Report(string stage, string? item, int done, int total) => _progress?.Report(new BackupProgress(stage, item, done, total, _bytes));

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await command.ExecuteNonQueryAsync(_cancel);
        }

        private async Task<object?> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            return await command.ExecuteScalarAsync(_cancel);
        }

        private async Task<List<string>> ReadStringsAsync(string sql)
        {
            var values = new List<string>();
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(_cancel);
            while (await reader.ReadAsync(_cancel))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }

        private void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The scratch folder {Directory} of the restore could not be removed: {Message}", directory, ex.Message);
            }
        }
    }
}

/// <summary>
/// Puts the restored files in place of the current ones: what the backup holds replaces the current top-level entries of the same
/// name (the old ones are moved aside, not deleted), anything else in the data volume stays. Can be undone until the old files are removed.
/// </summary>
internal sealed class FileSwap(string liveRoot, string stagedRoot, string previousRoot, ILogger logger)
{
    private readonly List<(string From, string To)> _moves = [];

    /// <summary>Putting the old files back did not work: they are in the folder they were moved to.</summary>
    public bool RevertFailed { get; private set; }

    public void Apply()
    {
        Directory.CreateDirectory(previousRoot);
        foreach (string staged in Directory.EnumerateFileSystemEntries(stagedRoot))
        {
            string name = Path.GetFileName(staged);
            string live = Path.Combine(liveRoot, name);
            if (Directory.Exists(live) || File.Exists(live))
            {
                Move(live, Path.Combine(previousRoot, name));
            }

            Move(staged, live);
        }
    }

    /// <summary>Undoes what <see cref="Apply"/> did so far (nothing, when it did nothing).</summary>
    public void Revert()
    {
        try
        {
            for (int i = _moves.Count - 1; i >= 0; i--)
            {
                MoveEntry(_moves[i].To, _moves[i].From);
            }

            _moves.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RevertFailed = true;
            logger.LogCritical(ex, "The old files could not be put back. They are in {Folder}; move them back into the data volume by hand.", previousRoot);
        }
    }

    private void Move(string from, string to)
    {
        MoveEntry(from, to);
        _moves.Add((from, to));
    }

    private static void MoveEntry(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Move(from, to);
            return;
        }

        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            // another volume (a scratch folder somewhere else): copy, then remove
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}

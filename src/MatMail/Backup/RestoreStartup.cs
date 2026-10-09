using MatMail.Configuration;
using MatMail.Services;
using MatMail.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace MatMail.Backup;

/// <summary>What the start-up decided: go on starting normally, or end the process with this exit code (a command of the command line was run).</summary>
public sealed record StartupOutcome(int? ExitCode, bool Restored)
{
    public static StartupOutcome Continue { get; } = new(null, false);
}

/// <summary>
/// Backup and restore before the application itself starts. Here the database and the files are not in use by anything, which is
/// what a restore needs. Four ways in:
/// <list type="bullet">
/// <item>a request left by the web interface (<see cref="PendingRestore"/>): the interface stops the program, the supervisor (Docker) starts it again, and the restore happens first;</item>
/// <item>the environment: <c>MATMAIL_RESTORE_FROM</c> (and <c>MATMAIL_RESTORE_PASSPHRASE</c>) restore that file into an installation that is still empty - a new server comes up with the old data;</item>
/// <item>the command line <c>--restore &lt;file&gt;</c> for a stopped installation;</item>
/// <item>the command line <c>--backup &lt;file&gt;</c>, a complete backup of a running or stopped one.</item>
/// </list>
/// </summary>
public static class RestoreStartup
{
    public const string RestoreFromVariable = "MATMAIL_RESTORE_FROM";
    public const string RestorePassphraseVariable = "MATMAIL_RESTORE_PASSPHRASE";

    private const int ExitUsage = 64;
    private const int ExitNoInput = 66;
    private const int ExitTemporaryFailure = 75;

    public static async Task<StartupOutcome> RunAsync(string[] args, AppConfig config, string dataDir, ILogger logger, CancellationToken cancel = default)
    {
        if (args.Contains("--backup"))
        {
            return new StartupOutcome(await BackupCommandAsync(args, config, dataDir, cancel), false);
        }

        if (args.Contains("--restore"))
        {
            return new StartupOutcome(await RestoreCommandAsync(args, config, dataDir, logger, cancel), false);
        }

        PendingRestore? pending = PendingRestore.Read(dataDir);
        if (pending is not null)
        {
            return await RunPendingAsync(pending, config, dataDir, logger, cancel);
        }

        string? from = Environment.GetEnvironmentVariable(RestoreFromVariable);
        if (!string.IsNullOrWhiteSpace(from))
        {
            return await RunFromEnvironmentAsync(from, config, dataDir, logger, cancel);
        }

        return StartupOutcome.Continue;
    }

    // ---------------------------------------------------------------------------------------------
    // A request of the web interface
    // ---------------------------------------------------------------------------------------------

    private static async Task<StartupOutcome> RunPendingAsync(PendingRestore pending, AppConfig config, string dataDir, ILogger logger, CancellationToken cancel)
    {
        // The request is gone first: a restore that kills the process must not be started again at every start.
        PendingRestore.Clear(dataDir);
        string path = pending.ResolvedBackupPath(dataDir);
        logger.LogWarning("Restoring the backup {File} (requested by {User} at {Time:u}).", path, pending.RequestedBy ?? "?", pending.RequestedUtc);

        string? passphrase = null;
        RestoreReport? early = null;
        try
        {
            passphrase = pending.Passphrase(Protector(dataDir));
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            early = new RestoreReport { Message = "The passphrase of the backup could not be read: " + ex.Message };
        }

        await using RestoreProgressHost? page = await RestoreProgressHost.StartAsync(config, dataDir, logger);
        RestoreReport report = early ?? await ExecuteAsync(
            config,
            dataDir,
            path,
            passphrase,
            pending.SafetyBackup,
            pending.HoldOutboundQueue,
            new Progress<BackupProgress>(p => page?.Report(p)),
            logger,
            cancel);
        report.RequestedBy = pending.RequestedBy;
        report.FinishedUtc = DateTime.UtcNow;
        report.Save(dataDir);
        page?.Finish(report.Succeeded, report.Message);

        if (pending.DeleteBackupAfterwards)
        {
            TryDelete(path);
        }

        return new StartupOutcome(null, report.Succeeded);
    }

    // ---------------------------------------------------------------------------------------------
    // MATMAIL_RESTORE_FROM: a new server with the data of the old one
    // ---------------------------------------------------------------------------------------------

    private static async Task<StartupOutcome> RunFromEnvironmentAsync(string from, AppConfig config, string dataDir, ILogger logger, CancellationToken cancel)
    {
        string path = Path.GetFullPath(from);
        if (!File.Exists(path))
        {
            logger.LogError("{Variable} points to {File}, which does not exist: nothing is restored.", RestoreFromVariable, path);
            return StartupOutcome.Continue;
        }

        await StartupInitializer.EnsureDatabaseExistsAsync(config.Database.ConnectionString, logger);
        if (await HasUsersAsync(config.Database.ConnectionString))
        {
            logger.LogInformation("{Variable} is set, but this installation already has data: nothing is restored.", RestoreFromVariable);
            return StartupOutcome.Continue;
        }

        logger.LogWarning("Restoring the backup {File} into the empty installation ({Variable}).", path, RestoreFromVariable);
        await using RestoreProgressHost? page = await RestoreProgressHost.StartAsync(config, dataDir, logger);
        RestoreReport report = await ExecuteAsync(
            config,
            dataDir,
            path,
            Environment.GetEnvironmentVariable(RestorePassphraseVariable),
            safetyBackup: false,
            holdQueue: true,
            new Progress<BackupProgress>(p => page?.Report(p)),
            logger,
            cancel);
        report.Save(dataDir);
        page?.Finish(report.Succeeded, report.Message);
        return new StartupOutcome(null, report.Succeeded);
    }

    // ---------------------------------------------------------------------------------------------
    // Command line
    // ---------------------------------------------------------------------------------------------

    private static async Task<int> BackupCommandAsync(string[] args, AppConfig config, string dataDir, CancellationToken cancel)
    {
        string? target = ValueOf(args, "--backup");
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("Usage: dotnet MatMail.dll --backup <file or folder> [--passphrase-env <VARIABLE>]");
            return ExitUsage;
        }

        string? passphrase = PassphraseOf(args, out int? passphraseError);
        if (passphraseError is not null)
        {
            return passphraseError.Value;
        }

        string path = Path.GetFullPath(target);
        if (Directory.Exists(path) || target.EndsWith('/') || target.EndsWith('\\'))
        {
            path = Path.Combine(path, BackupFiles.Name(DateTime.UtcNow, AppInfo.Version, passphrase is not null, BackupFiles.ManualLabel, await InstallationOrNullAsync(config.Database.ConnectionString)));
        }

        try
        {
            BackupFileResult result = await BackupJob.CreateFileAsync(
                new BackupSource(config.Database.ConnectionString, dataDir) { ExcludedDirectories = [Path.GetDirectoryName(path)!] },
                path,
                passphrase,
                Path.Combine(dataDir, "tmp"),
                ConsoleProgress("Backup"),
                verify: true,
                cancel);
            Console.WriteLine($"Backup written: {result.Path} ({result.Bytes / (1024.0 * 1024.0):0.0} MB, {result.Manifest.Database.Tables.Count} tables, {result.Manifest.Files.Count} files{(result.Encrypted ? ", encrypted" : string.Empty)}).");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or NpgsqlException or UnauthorizedAccessException or BackupCorruptException)
        {
            Console.Error.WriteLine("The backup failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RestoreCommandAsync(string[] args, AppConfig config, string dataDir, ILogger logger, CancellationToken cancel)
    {
        string? file = ValueOf(args, "--restore");
        if (string.IsNullOrWhiteSpace(file))
        {
            Console.Error.WriteLine("Usage: dotnet MatMail.dll --restore <file> [--passphrase-env <VARIABLE>] [--force] [--no-safety-backup] [--keep-queue]");
            return ExitUsage;
        }

        string? passphrase = PassphraseOf(args, out int? passphraseError);
        if (passphraseError is not null)
        {
            return passphraseError.Value;
        }

        string path = Path.GetFullPath(file);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{path} does not exist.");
            return ExitNoInput;
        }

        await StartupInitializer.EnsureDatabaseExistsAsync(config.Database.ConnectionString, logger);
        if (!args.Contains("--force"))
        {
            int others = await OtherSessionsAsync(config.Database.ConnectionString);
            if (others > 0)
            {
                Console.Error.WriteLine($"The database is in use by {others} other connection(s): stop MatMail first (or use --force when you know better).");
                return ExitTemporaryFailure;
            }
        }

        RestoreReport report = await ExecuteAsync(
            config,
            dataDir,
            path,
            passphrase,
            safetyBackup: !args.Contains("--no-safety-backup"),
            holdQueue: !args.Contains("--keep-queue"),
            ConsoleProgress("Restore"),
            logger,
            cancel);
        report.Save(dataDir);

        if (report.Succeeded)
        {
            Console.WriteLine($"Restored: {report.Message}.{(report.SafetyBackup is null ? string.Empty : $" The state before is saved as backups/{report.SafetyBackup}.")}");
            return 0;
        }

        Console.Error.WriteLine("The restore failed, nothing was changed: " + report.Message);
        return 1;
    }

    // ---------------------------------------------------------------------------------------------
    // The restore itself, the same for all of them
    // ---------------------------------------------------------------------------------------------

    private static async Task<RestoreReport> ExecuteAsync(
        AppConfig config,
        string dataDir,
        string backupPath,
        string? passphrase,
        bool safetyBackup,
        bool holdQueue,
        IProgress<BackupProgress>? progress,
        ILogger logger,
        CancellationToken cancel)
    {
        var report = new RestoreReport();
        try
        {
            string connectionString = config.Database.ConnectionString;
            await StartupInitializer.EnsureDatabaseExistsAsync(connectionString, logger);
            using BackupArchive archive = BackupArchive.Open(backupPath, passphrase);
            report.BackupCreatedUtc = archive.Manifest.CreatedUtc;
            report.BackupVersion = archive.Manifest.AppVersion;

            if (safetyBackup && await HasTablesAsync(connectionString))
            {
                report.SafetyBackup = await SaveCurrentStateAsync(connectionString, dataDir, passphrase, progress, cancel);
            }

            RestoreResult result = await BackupRestorer.RestoreAsync(
                archive,
                new RestoreOptions { ConnectionString = connectionString, DataDir = dataDir, HoldOutboundQueue = holdQueue },
                logger,
                progress,
                cancel);
            report.Succeeded = true;
            report.Message = $"{result.Tables} tables, {result.Rows} rows and {result.Files} files of the backup of {result.BackupCreatedUtc:yyyy-MM-dd HH:mm} UTC (version {result.BackupAppVersion}) in {result.Duration.TotalSeconds:0.#} s";
            logger.LogWarning("Restore done: {Message}.", report.Message);
        }
        catch (Exception ex) when (ex is BackupCorruptException or BackupPassphraseException or BackupIncompatibleException)
        {
            report.Message = ex.Message;
            logger.LogError("The restore was stopped, nothing was changed: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            report.Message = ex.Message;
            logger.LogError(ex, "The restore failed, nothing was changed.");
        }

        report.FinishedUtc = DateTime.UtcNow;
        return report;
    }

    /// <summary>
    /// A backup of what is there before it is replaced, in the backup folder, protected like the backup that replaces it. When it cannot
    /// be made the restore does not go on: a restore of the wrong backup could not be undone.
    /// </summary>
    private static async Task<string> SaveCurrentStateAsync(string connectionString, string dataDir, string? passphrase, IProgress<BackupProgress>? progress, CancellationToken cancel)
    {
        string folder = Path.Combine(dataDir, "backups");
        string name = BackupFiles.Name(DateTime.UtcNow, AppInfo.Version, passphrase is not null, BackupFiles.PreRestoreLabel, await InstallationOrNullAsync(connectionString));
        await BackupJob.CreateFileAsync(
            new BackupSource(connectionString, dataDir),
            Path.Combine(folder, name),
            passphrase,
            Path.Combine(dataDir, "tmp"),
            progress is null ? null : new Progress<BackupProgress>(p => progress.Report(p with { Stage = "safety-" + p.Stage })),
            verify: true,
            cancel);

        // the last three are kept
        foreach (FileInfo old in new DirectoryInfo(folder).EnumerateFiles()
                     .Where(f => BackupFiles.Parse(f.Name)?.Label == BackupFiles.PreRestoreLabel)
                     .OrderByDescending(f => BackupFiles.Parse(f.Name)!.CreatedUtc)
                     .Skip(3))
        {
            TryDelete(old.FullName);
        }

        return name;
    }

    // ---------------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>The id of the installation for the name of a file; none when the database cannot say (the name then carries zeros).</summary>
    private static async Task<string?> InstallationOrNullAsync(string connectionString)
    {
        try
        {
            return await SystemSettings.ReadInstallationIdAsync(connectionString);
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static IDataProtector Protector(string dataDir)
        => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDir, "keys")), builder => builder.SetApplicationName("MatMail")).CreateProtector(PendingRestore.Purpose);

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    /// <summary>The passphrase comes from an environment variable (<c>--passphrase-env NAME</c>), never from the command line, where every user of the machine can see it.</summary>
    private static string? PassphraseOf(string[] args, out int? error)
    {
        error = null;
        if (!args.Contains("--passphrase-env"))
        {
            return null;
        }

        string? name = ValueOf(args, "--passphrase-env");
        string? value = string.IsNullOrWhiteSpace(name) ? null : Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            Console.Error.WriteLine("--passphrase-env needs the name of an environment variable that holds the passphrase.");
            error = ExitUsage;
        }

        return value;
    }

    private static IProgress<BackupProgress> ConsoleProgress(string title)
    {
        string? last = null;
        return new Progress<BackupProgress>(p =>
        {
            string line = $"{title}: {p.Stage}";
            if (line != last)
            {
                last = line;
                Console.WriteLine(line + (p.Total > 0 ? $" ({p.Total})" : string.Empty));
            }
        });
    }

    private static async Task<bool> HasTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public')", connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> HasUsersAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('public.\"User\"') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync() is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM \"User\")", connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<int> OtherSessionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left behind: the next clean-up gets it
        }
    }
}

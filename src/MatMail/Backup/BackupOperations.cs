using System.Collections.Concurrent;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Backup;

public enum JobState
{
    Running,
    Ready,
    Failed,
}

/// <summary>A backup that is made to be downloaded in the browser (not one of a plan).</summary>
public sealed record DownloadStatus(Guid Token, JobState State, string? FileName, long Bytes, bool Encrypted, string? Message);

/// <summary>
/// "Download a backup now": the backup is made in scratch space as a file (it takes as long as it takes, long before a browser would
/// wait for a first byte), and when it is ready the browser fetches it. The file is removed after an hour.
/// </summary>
public sealed class BackupDownloads
{
    private sealed record Entry(string Path, bool Encrypted, DateTime Started)
    {
        public JobState State { get; set; } = JobState.Running;
        public string? FileName { get; set; }
        public long Bytes { get; set; }
        public string? Message { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly BackupCoordinator _coordinator;
    private readonly BackupStorageFactory _storages;
    private readonly AppConfig _config;
    private readonly ILogger<BackupDownloads> _logger;
    private readonly IHostApplicationLifetime? _lifetime;

    public BackupDownloads(BackupCoordinator coordinator, BackupStorageFactory storages, AppConfig config, ILogger<BackupDownloads> logger, IHostApplicationLifetime? lifetime = null)
    {
        _coordinator = coordinator;
        _storages = storages;
        _config = config;
        _logger = logger;
        _lifetime = lifetime;
    }

    private string Folder => Path.Combine(_storages.ScratchDirectory, "downloads");

    /// <summary>Starts making the backup; null when something else is running. The passphrase encrypts the backup when given.</summary>
    public Guid? Start(string? passphrase)
    {
        BackupLease? lease = _coordinator.TryBegin("download", _lifetime?.ApplicationStopping ?? CancellationToken.None);
        if (lease is null)
        {
            return null;
        }

        Guid token = Guid.NewGuid();
        bool encrypted = !string.IsNullOrEmpty(passphrase);
        var entry = new Entry(Path.Combine(Folder, token.ToString("N") + (encrypted ? BackupFiles.EncryptedExtension : BackupFiles.PlainExtension)), encrypted, DateTime.UtcNow);
        _entries[token] = entry;

        _ = Task.Run(async () =>
        {
            using (lease)
            {
                try
                {
                    string? installation = await SystemSettings.ReadInstallationIdAsync(_config.Database.ConnectionString);
                    string name = BackupFiles.Name(DateTime.UtcNow, AppInfo.Version, encrypted, BackupFiles.ManualLabel, installation);
                    BackupFileResult result = await BackupJob.CreateFileAsync(
                        new BackupSource(_config.Database.ConnectionString, _config.DataDir) { ExcludedDirectories = [Folder] },
                        entry.Path,
                        passphrase,
                        _storages.ScratchDirectory,
                        lease.Progress,
                        verify: true,
                        lease.Token);
                    entry.FileName = name;
                    entry.Bytes = result.Bytes;
                    entry.State = JobState.Ready;
                }
                catch (OperationCanceledException)
                {
                    entry.State = JobState.Failed;
                    entry.Message = "Cancelled.";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The backup to download failed.");
                    entry.State = JobState.Failed;
                    entry.Message = ex.Message;
                }
            }
        });
        return token;
    }

    public DownloadStatus? Get(Guid token)
        => _entries.TryGetValue(token, out Entry? entry) ? new DownloadStatus(token, entry.State, entry.FileName, entry.Bytes, entry.Encrypted, entry.Message) : null;

    /// <summary>The finished file and the name it should have on the disk of the person who downloads it; null when it is not (or no longer) there.</summary>
    public (string Path, string Name)? Ready(Guid token)
        => _entries.TryGetValue(token, out Entry? entry) && entry.State == JobState.Ready && File.Exists(entry.Path) ? (entry.Path, entry.FileName!) : null;

    public void Remove(Guid token)
    {
        if (_entries.TryRemove(token, out Entry? entry))
        {
            TryDelete(entry.Path);
        }
    }

    /// <summary>Removes downloads that were never fetched (or fetched and left): everything older than <paramref name="age"/>.</summary>
    public int CleanUp(TimeSpan age)
    {
        int removed = 0;
        foreach ((Guid token, Entry entry) in _entries.ToArray())
        {
            if (entry.State != JobState.Running && DateTime.UtcNow - entry.Started > age)
            {
                Remove(token);
                removed++;
            }
        }

        if (Directory.Exists(Folder))
        {
            foreach (FileInfo file in new DirectoryInfo(Folder).EnumerateFiles().Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > age + TimeSpan.FromHours(1)))
            {
                TryDelete(file.FullName);
                removed++;
            }
        }

        return removed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the next clean-up gets it
        }
    }
}

public enum PreparationState
{
    Idle,
    Running,
    Restarting,
    Failed,
}

public sealed record PreparationStatus(PreparationState State, string? Message, BackupActivity? Activity);

/// <summary>What is to be restored and how.</summary>
/// <param name="TargetId">A file of this target (with <paramref name="FileName"/>) …</param>
/// <param name="FileName">… of this name.</param>
/// <param name="Upload">… or a file that was uploaded for it (the id of the upload).</param>
public sealed record RestoreRequest(long? TargetId, string? FileName, Guid? Upload, string? Passphrase, bool SafetyBackup, bool HoldOutboundQueue, bool VerifyFirst, string RequestedBy);

/// <summary>
/// The first half of a restore, done by the running program: get the backup file (fetch it from a share), open it with its passphrase,
/// see that this version can restore it, optionally read all of it, then leave the request (<see cref="PendingRestore"/>) and stop the
/// program; the restore itself is done by the start-up (<see cref="RestoreStartup"/>). Everything that can be wrong with the backup shows
/// here, before anything is stopped.
/// </summary>
public sealed class RestorePreparation
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackupCoordinator _coordinator;
    private readonly BackupStorageFactory _storages;
    private readonly AppConfig _config;
    private readonly IDataProtectionProvider _protection;
    private readonly ILogger<RestorePreparation> _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly object _lock = new();
    private PreparationState _state = PreparationState.Idle;
    private string? _message;

    public RestorePreparation(
        IServiceScopeFactory scopes,
        BackupCoordinator coordinator,
        BackupStorageFactory storages,
        AppConfig config,
        IDataProtectionProvider protection,
        ILogger<RestorePreparation> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _scopes = scopes;
        _coordinator = coordinator;
        _storages = storages;
        _config = config;
        _protection = protection;
        _logger = logger;
        _lifetime = lifetime;
    }

    public PreparationStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new PreparationStatus(_state, _message, _state == PreparationState.Running ? _coordinator.Current : null);
            }
        }
    }

    /// <summary>Where uploaded backups wait: <c>restore/incoming/&lt;id&gt;.zip</c> (or <c>.mmbak</c>).</summary>
    public string NewUploadPath(Guid id, string originalName)
    {
        string extension = originalName.EndsWith(BackupFiles.EncryptedExtension, StringComparison.OrdinalIgnoreCase) ? BackupFiles.EncryptedExtension : BackupFiles.PlainExtension;
        Directory.CreateDirectory(PendingRestore.IncomingFolder(_config.DataDir));
        return Path.Combine(PendingRestore.IncomingFolder(_config.DataDir), id.ToString("N") + extension);
    }

    /// <summary>
    /// Takes a backup file that is sent as the body of a request (no form: a backup is gigabytes, and a form would be copied to a temporary
    /// file first). It appears under its id when it is whole and looks like a backup; the id is what the pages ask for.
    /// </summary>
    /// <exception cref="BackupCorruptException">It is no backup.</exception>
    public async Task<(Guid Id, long Bytes, bool Encrypted)> SaveUploadAsync(Stream body, string? originalName, CancellationToken cancel)
    {
        Guid id = Guid.NewGuid();
        string path = NewUploadPath(id, originalName ?? "backup.zip");
        string partial = path + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await body.CopyToAsync(file, cancel);
            }

            byte[] start = new byte[4];
            using (var check = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read = check.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
                if (read < 4 || !(BackupEncryption.LooksEncrypted(start) || (start[0] == 'P' && start[1] == 'K')))
                {
                    throw new BackupCorruptException("This is not a MatMail backup.");
                }
            }

            File.Move(partial, path);
            return (id, new FileInfo(path).Length, BackupFiles.IsEncrypted(path));
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next clean-up gets it
            }
        }
    }

    /// <summary>Removes an uploaded file that is not going to be restored after all.</summary>
    public void DiscardUpload(Guid id)
    {
        if (FindUpload(id) is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next clean-up gets it
            }
        }
    }

    public string? FindUpload(Guid id)
    {
        string folder = PendingRestore.IncomingFolder(_config.DataDir);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, id.ToString("N") + ".*").FirstOrDefault(f => !f.EndsWith(".partial", StringComparison.Ordinal)) : null;
    }

    /// <summary>Starts the preparation in the background. False when something else is running or the program is already about to restart.</summary>
    public bool Start(RestoreRequest request)
    {
        lock (_lock)
        {
            if (_state is PreparationState.Running or PreparationState.Restarting)
            {
                return false;
            }
        }

        BackupLease? lease = _coordinator.TryBegin("restore", _lifetime?.ApplicationStopping ?? CancellationToken.None);
        if (lease is null)
        {
            return false;
        }

        Set(PreparationState.Running, null);
        _ = Task.Run(async () =>
        {
            using (lease)
            {
                await PrepareAsync(request, lease);
            }
        });
        return true;
    }

    private async Task PrepareAsync(RestoreRequest request, BackupLease lease)
    {
        string? downloaded = null;
        bool restarting = false;
        try
        {
            (string path, bool deleteAfterwards) = await ObtainAsync(request, lease);
            downloaded = deleteAfterwards ? path : null;

            using (BackupArchive archive = BackupArchive.Open(path, request.Passphrase))
            {
                await CheckCompatibleAsync(archive);
                if (request.VerifyFirst)
                {
                    await archive.VerifyAsync(lease.Progress, lease.Token);
                }
            }

            IDataProtector protector = _protection.CreateProtector(PendingRestore.Purpose);
            new PendingRestore
            {
                BackupPath = path,
                DeleteBackupAfterwards = deleteAfterwards,
                ProtectedPassphrase = PendingRestore.Protect(protector, request.Passphrase),
                SafetyBackup = request.SafetyBackup,
                HoldOutboundQueue = request.HoldOutboundQueue,
                RequestedBy = request.RequestedBy,
                RequestedUtc = DateTime.UtcNow,
            }.Save(_config.DataDir);

            _logger.LogWarning("A restore of {File} was asked for by {User}; the program stops to carry it out.", path, request.RequestedBy);
            Set(PreparationState.Restarting, null);
            restarting = true;

            // the page has to see "restarting" before the program goes
            await Task.Delay(TimeSpan.FromSeconds(3));
            _lifetime?.StopApplication();
        }
        catch (OperationCanceledException)
        {
            Set(PreparationState.Failed, "Cancelled.");
        }
        catch (Exception ex) when (ex is BackupCorruptException or BackupPassphraseException or BackupIncompatibleException or BackupStorageException or IOException)
        {
            Set(PreparationState.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The restore could not be prepared.");
            Set(PreparationState.Failed, ex.Message);
        }
        finally
        {
            if (!restarting && downloaded is not null)
            {
                try
                {
                    File.Delete(downloaded);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // the next clean-up gets it
                }
            }
        }
    }

    /// <summary>The file to restore: in place when it is a folder of the server, fetched from a share, or the upload.</summary>
    private async Task<(string Path, bool DeleteAfterwards)> ObtainAsync(RestoreRequest request, BackupLease lease)
    {
        if (request.Upload is Guid upload)
        {
            return (FindUpload(upload) ?? throw new BackupStorageException("The uploaded file is not there any more: upload it again."), true);
        }

        if (request.TargetId is not long targetId || string.IsNullOrEmpty(request.FileName))
        {
            throw new BackupStorageException("No backup was chosen.");
        }

        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<Services.CurrentUser>().RunAsSystem();
        BackupTarget target = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId, lease.Token)
            ?? throw new BackupStorageException("The target does not exist any more.");
        using IBackupStorage storage = _storages.Create(target);
        BackupStorageNames.Require(request.FileName);

        if (storage.LocalFolder is { } folder)
        {
            string local = Path.Combine(folder, request.FileName);
            return File.Exists(local) ? (local, false) : throw new BackupStorageException($"“{request.FileName}” is not in {folder} any more.");
        }

        string extension = request.FileName.EndsWith(BackupFiles.EncryptedExtension, StringComparison.OrdinalIgnoreCase) ? BackupFiles.EncryptedExtension : BackupFiles.PlainExtension;
        Directory.CreateDirectory(PendingRestore.IncomingFolder(_config.DataDir));
        string path = Path.Combine(PendingRestore.IncomingFolder(_config.DataDir), Guid.NewGuid().ToString("N") + extension);
        var progress = new Progress<long>(bytes => lease.Progress.Report(new BackupProgress("fetch", request.FileName, 0, 0, bytes)));
        try
        {
            await storage.DownloadAsync(request.FileName, path, progress, lease.Token);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next clean-up gets it
            }

            throw;
        }

        return (path, true);
    }

    private async Task CheckCompatibleAsync(BackupArchive archive)
    {
        using IServiceScope scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        string? why = VersionGuard.WhyNotRestorable(
            archive.Manifest.Database.SchemaVersion,
            archive.Manifest.Database.DataVersion,
            archive.Manifest.Format,
            BackupFormat.Version,
            db.Database.GetMigrations(),
            DataMigrations.Latest);
        if (why is not null)
        {
            throw new BackupIncompatibleException(why);
        }

        if (archive.Manifest.Database.SchemaVersion is null)
        {
            throw new BackupIncompatibleException("The backup has no database.");
        }

        await Task.CompletedTask;
    }

    private void Set(PreparationState state, string? message)
    {
        lock (_lock)
        {
            _state = state;
            _message = message;
        }
    }

    /// <summary>Removes uploaded or fetched backups that nobody went on with (older than <paramref name="age"/>), unless a restore is under way.</summary>
    public int CleanUp(TimeSpan age)
    {
        lock (_lock)
        {
            if (_state is PreparationState.Running or PreparationState.Restarting)
            {
                return 0;
            }
        }

        string folder = PendingRestore.IncomingFolder(_config.DataDir);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int removed = 0;
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles().Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > age))
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next clean-up gets it
            }
        }

        return removed;
    }

    /// <summary>Forgets a failure (after it was shown).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_state == PreparationState.Failed)
            {
                _state = PreparationState.Idle;
                _message = null;
            }
        }
    }
}

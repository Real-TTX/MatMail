using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;

namespace MatMail.Backup;

/// <summary>A backup file in a target.</summary>
public sealed record RemoteBackupFile(string Name, long Bytes, DateTime ModifiedUtc);

/// <summary>What a test of a target found.</summary>
public sealed record StorageCheck(bool Ok, string Message, long? FreeBytes);

/// <summary>A target cannot be reached or does not do what was asked; the message says why in words an administrator can act on.</summary>
public sealed class BackupStorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A place backups are kept: a folder of the server or an SMB share. Files are addressed by their plain name (no folders).</summary>
public interface IBackupStorage : IDisposable
{
    /// <summary>The folder when this is a folder of the server: backups are then made right into it.</summary>
    string? LocalFolder { get; }

    /// <summary>Connects, makes sure the folder is there and can be written to, and says how much room is left.</summary>
    Task<StorageCheck> CheckAsync(CancellationToken cancel);

    Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken cancel);

    /// <summary>Writes a file under its name (it appears whole or not at all; an existing file is not replaced).</summary>
    Task UploadAsync(string localPath, string name, IProgress<long>? progress, CancellationToken cancel);

    Task DownloadAsync(string name, string localPath, IProgress<long>? progress, CancellationToken cancel);

    /// <summary>The content of a file as a stream that is read while it arrives (a download in the browser); disposing it closes the connection.</summary>
    Task<Stream> OpenReadAsync(string name, CancellationToken cancel);

    Task DeleteAsync(string name, CancellationToken cancel);
}

/// <summary>Which folders of the server a local target may be.</summary>
public static class LocalTargetPolicy
{
    /// <summary>
    /// A folder outside the data volume (a mounted disk or share), or one inside <c>backups</c> of the data volume. Anywhere else in the
    /// data volume would make every backup contain the backups before it, and a restore would move them aside.
    /// </summary>
    public static string? Validate(string? path, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "The folder is missing.";
        }

        if (!Path.IsPathRooted(path))
        {
            return "The folder must be a full path, e.g. /backups.";
        }

        if (path.Split('/', '\\').Contains(".."))
        {
            return "The folder must not contain “..”.";
        }

        string full = Path.GetFullPath(path);
        string data = Path.GetFullPath(dataDir);
        string allowed = Path.Combine(data, "backups");
        if (IsInside(full, data) && !IsInside(full, allowed))
        {
            return $"Inside the data volume only {allowed} may be used (anywhere else the backups would end up in the next backup).";
        }

        return null;
    }

    public static bool IsInside(string path, string folder)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(normalizedPath, normalizedFolder, comparison)
               || normalizedPath.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, comparison);
    }
}

public static class BackupStorageNames
{
    /// <summary>A file name without anything that could lead out of the folder.</summary>
    public static bool IsPlainName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && name is not ("." or "..") && name.Length <= 255;

    public static void Require(string name)
    {
        if (!IsPlainName(name))
        {
            throw new BackupStorageException($"“{name}” is not a plain file name.");
        }
    }
}

/// <summary>A folder of the server.</summary>
public sealed class LocalBackupStorage(string folder) : IBackupStorage
{
    public string? LocalFolder => Path.GetFullPath(folder);

    public Task<StorageCheck> CheckAsync(CancellationToken cancel)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".matmail-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new StorageCheck(false, $"The folder {folder} cannot be written to: {ex.Message}", null));
        }

        long? free = FreeSpace();
        return Task.FromResult(new StorageCheck(true, $"The folder {folder} can be written to.", free));
    }

    private long? FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken cancel)
    {
        if (!Directory.Exists(folder))
        {
            return Task.FromResult<IReadOnlyList<RemoteBackupFile>>([]);
        }

        IReadOnlyList<RemoteBackupFile> files = new DirectoryInfo(folder).EnumerateFiles()
            .Select(f => new RemoteBackupFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
        return Task.FromResult(files);
    }

    public async Task UploadAsync(string localPath, string name, IProgress<long>? progress, CancellationToken cancel)
    {
        BackupStorageNames.Require(name);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name);
        if (File.Exists(target))
        {
            throw new BackupStorageException($"“{name}” exists already in {folder}.");
        }

        string partial = target + ".partial";
        try
        {
            await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[128 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    copied += read;
                    progress?.Report(copied);
                }
            }

            File.Move(partial, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupStorageException($"“{name}” could not be written to {folder}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public async Task DownloadAsync(string name, string localPath, IProgress<long>? progress, CancellationToken cancel)
    {
        BackupStorageNames.Require(name);
        string source = Path.Combine(folder, name);
        if (!File.Exists(source))
        {
            throw new BackupStorageException($"“{name}” is not in {folder}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            copied += read;
            progress?.Report(copied);
        }
    }

    public Task<Stream> OpenReadAsync(string name, CancellationToken cancel)
    {
        BackupStorageNames.Require(name);
        string source = Path.Combine(folder, name);
        if (!File.Exists(source))
        {
            throw new BackupStorageException($"“{name}” is not in {folder}.");
        }

        return Task.FromResult<Stream>(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous));
    }

    public Task DeleteAsync(string name, CancellationToken cancel)
    {
        BackupStorageNames.Require(name);
        try
        {
            File.Delete(Path.Combine(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupStorageException($"“{name}” could not be deleted from {folder}: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover .partial file is not a backup and is removed by the next attempt
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Makes the storage of a target.</summary>
public sealed class BackupStorageFactory(SecretProtector secrets, AppConfig config)
{
    /// <param name="target">The target.</param>
    /// <param name="plainPassword">The password as typed in a form that is not saved yet; otherwise the stored one is used.</param>
    public IBackupStorage Create(BackupTarget target, string? plainPassword = null)
    {
        switch (target.Kind)
        {
            case BackupTargetKind.Local:
                string? problem = LocalTargetPolicy.Validate(target.Path, config.DataDir);
                return problem is null ? new LocalBackupStorage(target.Path) : throw new BackupStorageException(problem);

            case BackupTargetKind.Smb:
                return new SmbBackupStorage(new SmbTargetOptions(
                    target.Host ?? string.Empty,
                    target.Share ?? string.Empty,
                    target.Path,
                    target.Domain,
                    target.Username,
                    plainPassword ?? secrets.Unprotect(target.PasswordProtected)));

            default:
                throw new BackupStorageException($"Unknown kind of target: {target.Kind}.");
        }
    }

    /// <summary>Where a backup is written first: the scratch space of the installation.</summary>
    public string ScratchDirectory => string.IsNullOrWhiteSpace(config.Backup.TempDirectory) ? Path.Combine(config.DataDir, "tmp") : config.Backup.TempDirectory;
}

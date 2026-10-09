namespace MatMail.Backup;

public sealed record BackupFileResult(string Path, long Bytes, bool Encrypted, BackupManifest Manifest);

/// <summary>Makes a backup file: the archive is written to a scratch file, checked, encrypted when a passphrase is given, and only then it gets its name.</summary>
public static class BackupJob
{
    /// <param name="source">What to back up.</param>
    /// <param name="destinationPath">The file to make. It must not exist; it appears complete or not at all (it is written under another name first).</param>
    /// <param name="passphrase">Encrypts the backup when given.</param>
    /// <param name="scratchDirectory">Where the archive is written first (needs room for the backup, twice when it is encrypted).</param>
    /// <param name="verify">Read the finished archive back and compare it with its manifest before it is accepted.</param>
    public static async Task<BackupFileResult> CreateFileAsync(
        BackupSource source,
        string destinationPath,
        string? passphrase,
        string scratchDirectory,
        IProgress<BackupProgress>? progress = null,
        bool verify = true,
        CancellationToken cancel = default)
    {
        destinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"{destinationPath} exists already.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        Directory.CreateDirectory(scratchDirectory);
        string scratch = Path.Combine(scratchDirectory, "backup-" + Guid.NewGuid().ToString("N")[..10] + ".zip");
        string partial = destinationPath + ".partial";

        try
        {
            BackupManifest manifest;
            await using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                manifest = await BackupWriter.WriteAsync(source, file, progress, cancel);
            }

            if (verify)
            {
                using BackupArchive archive = BackupArchive.Open(scratch);
                await archive.VerifyAsync(progress is null ? null : new Progress<BackupProgress>(p => progress.Report(p with { Stage = "verify" })), cancel);
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                File.Move(scratch, partial, overwrite: true);
            }
            else
            {
                await BackupFiles.EncryptAsync(scratch, partial, passphrase, cancel);
            }

            File.Move(partial, destinationPath);
            return new BackupFileResult(destinationPath, new FileInfo(destinationPath).Length, !string.IsNullOrEmpty(passphrase), manifest);
        }
        finally
        {
            TryDelete(scratch);
            TryDelete(partial);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // scratch space: the next clean-up gets it
        }
    }
}

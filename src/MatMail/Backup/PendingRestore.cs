using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatMail.Backup;

/// <summary>
/// The request to restore a backup the next time the program starts: a restore needs the database and the files to itself, so the
/// running program only notes what is to be done and stops; the start-up does it before anything else is running (see <see cref="RestoreStartup"/>).
/// </summary>
public sealed class PendingRestore
{
    /// <summary>Purpose string of the data protector that keeps the passphrase of the backup out of the file in plain text.</summary>
    public const string Purpose = "MatMail.PendingRestore";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Full path of the backup file, or relative to the data volume.</summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>The file was uploaded for this restore (or fetched from a backup target): it is removed once the restore is done.</summary>
    public bool DeleteBackupAfterwards { get; set; }

    /// <summary>The passphrase of an encrypted backup, protected with the key ring of the installation (which still is the old one while the request waits).</summary>
    public string? ProtectedPassphrase { get; set; }

    /// <summary>Back up what is there now before it is replaced (kept in the backup folder), so that a restore of the wrong backup can be undone.</summary>
    public bool SafetyBackup { get; set; } = true;

    /// <summary>See <see cref="RestoreOptions.HoldOutboundQueue"/>.</summary>
    public bool HoldOutboundQueue { get; set; } = true;

    public string? RequestedBy { get; set; }
    public DateTime RequestedUtc { get; set; }

    public static string Folder(string dataDir) => Path.Combine(dataDir, "restore");
    public static string MarkerPath(string dataDir) => Path.Combine(Folder(dataDir), "pending.json");
    public static string ResultPath(string dataDir) => Path.Combine(Folder(dataDir), "result.json");

    /// <summary>Where a backup that is uploaded for a restore waits.</summary>
    public static string IncomingFolder(string dataDir) => Path.Combine(Folder(dataDir), "incoming");

    public string ResolvedBackupPath(string dataDir) => Path.GetFullPath(Path.Combine(dataDir, BackupPath));

    public string? Passphrase(IDataProtector protector) => ProtectedPassphrase is null ? null : protector.Unprotect(ProtectedPassphrase);

    public static string? Protect(IDataProtector protector, string? passphrase) => string.IsNullOrEmpty(passphrase) ? null : protector.Protect(passphrase);

    /// <summary>Writes the request (atomically).</summary>
    public void Save(string dataDir)
    {
        Directory.CreateDirectory(Folder(dataDir));
        string temp = MarkerPath(dataDir) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, MarkerPath(dataDir), overwrite: true);
    }

    /// <summary>The request that waits, or null (also for one that cannot be read: it is of no use and would be tried at every start).</summary>
    public static PendingRestore? Read(string dataDir)
    {
        string path = MarkerPath(dataDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Clear(string dataDir)
    {
        string path = MarkerPath(dataDir);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>How the last restore ended; the backup page shows it. Kept in the data volume because the database it belongs to was replaced.</summary>
public sealed class RestoreReport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool Succeeded { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string? Message { get; set; }
    public DateTime? BackupCreatedUtc { get; set; }
    public string? BackupVersion { get; set; }

    /// <summary>File name of the backup of the state before the restore (in the backup folder), when one was made.</summary>
    public string? SafetyBackup { get; set; }

    public string? RequestedBy { get; set; }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(PendingRestore.Folder(dataDir));
        File.WriteAllText(PendingRestore.ResultPath(dataDir), JsonSerializer.Serialize(this, Json));
    }

    public static RestoreReport? Read(string dataDir)
    {
        string path = PendingRestore.ResultPath(dataDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreReport>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Clear(string dataDir)
    {
        string path = PendingRestore.ResultPath(dataDir);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

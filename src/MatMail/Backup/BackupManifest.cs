using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatMail.Backup;

/// <summary>What the format of a backup is made of (see <see cref="BackupManifest"/>).</summary>
public static class BackupFormat
{
    /// <summary>
    /// The layout of the archive. A change of the layout raises it, and a program reads every number up to its own: an old backup
    /// stays readable. (The schema of the database and the layout of the data files have numbers of their own, see Versioning.)
    /// </summary>
    public const int Version = 1;

    public const string ManifestEntry = "manifest.json";
    public const string DatabasePrefix = "db/";
    public const string DataPrefix = "data/";

    /// <summary>A table is written in parts of this size (uncompressed), so that no single entry of the archive grows beyond what every zip reader copes with.</summary>
    public const long PartBytes = 512L * 1024 * 1024;

    /// <summary>
    /// What of the data volume is not backed up: scratch space, the backups themselves and what a restore leaves there. Everything else under the data volume is,
    /// including what future versions put there – a new kind of file needs no change here, a new place outside the volume would not be saved.
    /// </summary>
    public static readonly string[] ExcludedTopLevel = ["tmp", "backups", "restore"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The table of contents of a backup: who made it, with which version, and every part with its size and checksum.</summary>
public sealed class BackupManifest
{
    public int Format { get; set; } = BackupFormat.Version;
    public DateTime CreatedUtc { get; set; }

    /// <summary>The id of the installation that made it (<c>SystemSetting</c> "InstallationId").</summary>
    public string? InstallationId { get; set; }

    /// <summary>The version of the program that made it, e.g. 0.1.42-20261007.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public string? Host { get; set; }
    public ManifestDatabase Database { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = [];

    /// <summary>What a person should know about this backup (what it does not contain).</summary>
    public List<string> Notes { get; set; } = [];
}

public sealed class ManifestDatabase
{
    public string Provider { get; set; } = "PostgreSQL";
    public string? ServerVersion { get; set; }

    /// <summary>The last migration of the schema (EF Core migration id); a restore builds exactly this schema, loads the data and then migrates forward.</summary>
    public string? SchemaVersion { get; set; }

    public List<string> AppliedMigrations { get; set; } = [];

    /// <summary>The layout of the data files at the time (see <c>DataMigrations</c>).</summary>
    public int DataVersion { get; set; } = 1;

    public List<ManifestTable> Tables { get; set; } = [];
    public List<ManifestSequence> Sequences { get; set; } = [];
}

public sealed class ManifestTable
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public long Rows { get; set; }
    public List<ManifestPart> Parts { get; set; } = [];
}

/// <summary>An entry of the archive: its name, the size and the SHA-256 of its content (uncompressed).</summary>
public sealed class ManifestPart
{
    public string Entry { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ManifestFile
{
    /// <summary>Relative to the data volume, with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime ModifiedUtc { get; set; }
}

public sealed class ManifestSequence
{
    public string Name { get; set; } = string.Empty;
    public long? LastValue { get; set; }
}

/// <summary>A backup is damaged or incomplete (a checksum does not match, an entry is missing, the container is cut off).</summary>
public sealed class BackupCorruptException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The passphrase of an encrypted backup is missing or wrong.</summary>
public sealed class BackupPassphraseException(string message) : Exception(message);

/// <summary>The backup cannot be restored by this version of MatMail (it comes from a newer one).</summary>
public sealed class BackupIncompatibleException(string message) : Exception(message);

/// <summary>How far a backup or a restore is.</summary>
public sealed record BackupProgress(string Stage, string? Item, int Done, int Total, long Bytes);

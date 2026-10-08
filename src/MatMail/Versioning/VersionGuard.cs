namespace MatMail.Versioning;

/// <summary>The program meets data that a newer program made: it must not touch it.</summary>
public sealed class IncompatibleVersionException(string message) : Exception(message);

/// <summary>
/// Old data may be used by a newer program (it is migrated), never the other way round: a database that a newer release has
/// upgraded holds columns and tables this program does not know, and writing to it would damage it.
/// </summary>
public static class VersionGuard
{
    /// <summary>Refuses a database whose history has migrations this program does not have, and data files of a newer layout.</summary>
    public static void EnsureNotNewer(IEnumerable<string> appliedMigrations, IEnumerable<string> knownMigrations, int dataVersion, int knownDataVersion)
    {
        HashSet<string> known = knownMigrations.ToHashSet(StringComparer.Ordinal);
        string[] unknown = appliedMigrations.Where(m => !known.Contains(m)).ToArray();
        if (unknown.Length > 0)
        {
            throw new IncompatibleVersionException(
                $"The database was upgraded by a newer MatMail (it has the migration {unknown[^1]}, which this version {AppInfo.Version} does not know). "
                + "Start the newer version again, or restore a backup that was made with this or an older version.");
        }

        if (dataVersion > knownDataVersion)
        {
            throw new IncompatibleVersionException(
                $"The data of this installation has the layout {dataVersion}, this version {AppInfo.Version} only knows up to {knownDataVersion}. "
                + "Start the newer version again, or restore a backup that was made with this or an older version.");
        }
    }

    /// <summary>The answer to "can this program restore a backup with this schema and these data files?": null when yes, the reason when not.</summary>
    public static string? WhyNotRestorable(string? backupSchema, int backupDataVersion, int backupFormat, int knownFormat, IEnumerable<string> knownMigrations, int knownDataVersion)
    {
        if (backupFormat > knownFormat)
        {
            return $"The backup has the format {backupFormat}; this version {AppInfo.Version} reads up to {knownFormat}. It was made by a newer MatMail: update first.";
        }

        if (backupSchema is not null && !knownMigrations.Contains(backupSchema, StringComparer.Ordinal))
        {
            return $"The database in the backup is newer than this version {AppInfo.Version} (migration {backupSchema} is unknown). Restore it with the version that made it, or a newer one.";
        }

        if (backupDataVersion > knownDataVersion)
        {
            return $"The files in the backup have the layout {backupDataVersion}; this version {AppInfo.Version} only knows up to {knownDataVersion}. Restore it with a newer version.";
        }

        return null;
    }
}

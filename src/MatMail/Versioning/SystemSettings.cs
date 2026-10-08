using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatMail.Versioning;

/// <summary>The facts about the installation that are kept in the database (<see cref="SystemSetting"/>).</summary>
public static class SystemSettings
{
    /// <summary>Identifies this installation; a backup carries it, so a restore can tell whose backup it holds.</summary>
    public const string InstallationId = "InstallationId";

    /// <summary>The version of the program that started last (informational; the database compatibility is judged by the migrations).</summary>
    public const string AppVersion = "AppVersion";

    /// <summary>The version of the files in the data volume (see <see cref="DataMigrations"/>).</summary>
    public const string DataVersion = "DataVersion";

    /// <summary>When and from which backup this database was restored last (JSON); only informational.</summary>
    public const string LastRestore = "LastRestore";

    public static async Task<string?> GetAsync(MatMailDbContext db, string key, CancellationToken cancel = default)
        => await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancel);

    public static async Task SetAsync(MatMailDbContext db, string key, string value, CancellationToken cancel = default)
    {
        SystemSetting? row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancel);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else if (row.Value != value)
        {
            row.Value = value;
        }

        await db.SaveChangesAsync(cancel);
    }

    /// <summary>The id of the installation straight from a database (no application around it: the command line, a restore); null when there is none yet.</summary>
    public static async Task<string?> ReadInstallationIdAsync(string connectionString, CancellationToken cancel = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancel);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('public.\"SystemSetting\"') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(cancel) is not true)
            {
                return null;
            }
        }

        await using var command = new NpgsqlCommand($"SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = '{InstallationId}'", connection);
        return await command.ExecuteScalarAsync(cancel) as string;
    }

    /// <summary>The version of the files as the database knows it; 1 for an installation that has never recorded one (the layout of the first release).</summary>
    public static async Task<int> GetDataVersionAsync(MatMailDbContext db, CancellationToken cancel = default)
        => int.TryParse(await GetAsync(db, DataVersion, cancel), out int version) && version > 0 ? version : 1;

    /// <summary>The id of this installation, made on first use.</summary>
    public static async Task<string> GetOrCreateInstallationIdAsync(MatMailDbContext db, CancellationToken cancel = default)
    {
        string? existing = await GetAsync(db, InstallationId, cancel);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        string created = Guid.NewGuid().ToString("D");
        await SetAsync(db, InstallationId, created, cancel);
        return created;
    }
}

using Npgsql;

namespace MatMail.Versioning;

/// <summary>What a data migration may touch: the files of the data volume and, within the same transaction, the database.</summary>
public sealed class DataMigrationContext
{
    /// <summary>The data volume as it is to be migrated: the live one at start-up, the staged copy while a backup is restored.</summary>
    public required string DataDir { get; init; }

    public required NpgsqlConnection Connection { get; init; }

    /// <summary>The transaction the database part belongs to (a restore runs everything in one), or null.</summary>
    public NpgsqlTransaction? Transaction { get; init; }

    public required ILogger Logger { get; init; }

    public async Task<int> ExecuteAsync(string sql, CancellationToken cancel = default)
    {
        await using var command = new NpgsqlCommand(sql, Connection, Transaction);
        return await command.ExecuteNonQueryAsync(cancel);
    }
}

/// <summary>One step from version <c>Version - 1</c> of the files (and the data in the database that belongs to them) to <c>Version</c>.</summary>
public sealed record DataMigration(int Version, string Description, Func<DataMigrationContext, Task> Apply);

/// <summary>
/// Everything that is not the schema of the database but changes between releases: the layout of the data volume, the format of
/// <c>config/app.json</c>, data in the database that needs more than a column change. The schema itself is the job of the EF
/// Core migrations; the two together are what lets an old backup be restored by a newer program.
///
/// A change of that kind gets the next number here and a step that brings the previous layout to the new one. Nothing else needs
/// to be remembered: the number is stored with the installation and with every backup, and a restore runs the steps between the
/// number of the backup and the current one over the restored files before they are put in place. The first release has number 1;
/// no step leads to it.
/// </summary>
public static class DataMigrations
{
    /// <summary>The steps, ordered by version (consecutive, starting at 2).</summary>
    public static readonly IReadOnlyList<DataMigration> All = [];

    /// <summary>The version of the files this program writes and expects.</summary>
    public static int Latest => LatestOf(All);

    public static int LatestOf(IReadOnlyList<DataMigration> steps) => steps.Count == 0 ? 1 : steps.Max(s => s.Version);

    /// <summary>The steps that lead from <paramref name="from"/> to the latest version, in order; fails on a gap or a duplicate in the list.</summary>
    public static IReadOnlyList<DataMigration> Pending(int from, IReadOnlyList<DataMigration>? steps = null)
    {
        steps ??= All;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Version != i + 2)
            {
                throw new InvalidOperationException($"The data migrations must be numbered 2, 3, 4 … without gaps; the step at position {i + 1} has number {steps[i].Version}.");
            }
        }

        return steps.Where(s => s.Version > from).OrderBy(s => s.Version).ToList();
    }

    /// <summary>Runs what is due; returns the version the files have afterwards.</summary>
    public static async Task<int> RunAsync(int from, DataMigrationContext context, IReadOnlyList<DataMigration>? steps = null)
    {
        int version = from;
        foreach (DataMigration step in Pending(from, steps))
        {
            context.Logger.LogInformation("Data migration {Version}: {Description}", step.Version, step.Description);
            await step.Apply(context);
            version = step.Version;
        }

        return version;
    }
}

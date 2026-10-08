using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MatMail.Tests;

/// <summary>A fresh PostgreSQL container has only the "postgres" database: the application creates its own on the first start.</summary>
public sealed class StartupDatabaseTests
{
    [DbFact]
    public async Task A_missing_database_is_created_and_a_second_call_changes_nothing()
    {
        string name = "Matmail-Start_" + Guid.NewGuid().ToString("N")[..10];   // upper case and a hyphen: the name has to be quoted
        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.AdminConnectionString) { Database = name, Pooling = false };
        try
        {
            Assert.False(await DatabaseExistsAsync(name));

            await StartupInitializer.EnsureDatabaseExistsAsync(builder.ConnectionString, NullLogger.Instance);
            Assert.True(await DatabaseExistsAsync(name));

            await StartupInitializer.EnsureDatabaseExistsAsync(builder.ConnectionString, NullLogger.Instance);
            Assert.True(await DatabaseExistsAsync(name));

            // and it is a database one can connect to
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
        }
        finally
        {
            await RunAdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task A_server_that_is_not_there_is_waited_for_and_then_left_to_the_migration()
    {
        // nobody listens on port 1: the check keeps trying for the time it is given, then returns without throwing
        // (the retry loop of the migration reports the problem)
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await StartupInitializer.EnsureDatabaseExistsAsync("Host=127.0.0.1;Port=1;Database=matmail;Username=postgres;Password=x;Timeout=2", NullLogger.Instance, TimeSpan.FromSeconds(3));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2.5), $"gave up after {watch.Elapsed}");
    }

    [Fact]
    public async Task Nothing_to_check_is_not_an_error()
    {
        await StartupInitializer.EnsureDatabaseExistsAsync(null, NullLogger.Instance, TimeSpan.FromSeconds(1));
        await StartupInitializer.EnsureDatabaseExistsAsync("Host=127.0.0.1;Port=1", NullLogger.Instance, TimeSpan.FromSeconds(1));
    }

    [DbFact]
    public async Task A_refused_password_does_not_wait()
    {
        // a server that answers "no" will not answer "yes" in a minute: the migration reports it, the check does not hold the start-up
        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.AdminConnectionString) { Database = "matmail_never_created", Password = "not-the-password", Pooling = false };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await StartupInitializer.EnsureDatabaseExistsAsync(builder.ConnectionString, NullLogger.Instance, TimeSpan.FromSeconds(30));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
        Assert.False(await DatabaseExistsAsync("matmail_never_created"));
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task RunAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

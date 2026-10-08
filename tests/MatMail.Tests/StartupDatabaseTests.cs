using System.Net.Sockets;
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
            await DropQuietlyAsync(name);
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

    [Fact]
    public void A_server_that_is_starting_or_busy_is_waited_for()
    {
        Assert.True(StartupInitializer.IsServerNotReady(new NpgsqlException("connection refused", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(StartupInitializer.IsServerNotReady(new NpgsqlException("host unknown", new SocketException((int)SocketError.HostNotFound))));
        Assert.True(StartupInitializer.IsServerNotReady(new TimeoutException()));
        Assert.True(StartupInitializer.IsServerNotReady(Postgres("57P03")));   // the database system is starting up
        Assert.True(StartupInitializer.IsServerNotReady(Postgres("53300")));   // too many clients already
    }

    [Fact]
    public void A_server_that_answers_no_is_not_waited_for()
    {
        // it will not answer "yes" in a minute: the migration reports it, the check does not hold the start-up
        Assert.False(StartupInitializer.IsServerNotReady(Postgres("28P01")));  // password authentication failed
        Assert.False(StartupInitializer.IsServerNotReady(Postgres("28000")));  // the role does not exist
        Assert.False(StartupInitializer.IsServerNotReady(Postgres("42501")));  // insufficient privilege
        Assert.False(StartupInitializer.IsServerNotReady(Postgres("3D000")));  // the database does not exist
        Assert.False(StartupInitializer.IsServerNotReady(new InvalidOperationException("something else")));
    }

    private static PostgresException Postgres(string sqlState) => new("test", "FATAL", "FATAL", sqlState);

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    /// <summary>A server that is busy with a hundred other test databases must not turn the clean-up into a failure of the test.</summary>
    private static async Task DropQuietlyAsync(string name)
    {
        try
        {
            await using var connection = new NpgsqlConnection(TestDatabase.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // left behind: harmless, the next call of the test uses another name
        }
    }
}

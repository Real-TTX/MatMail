using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using MatMail.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MatMail.Tests;

/// <summary>What the start-up does with the versions: notes them down, and never touches data of a newer program.</summary>
public class StartupVersionTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> SettingAsync(string key)
    {
        using IServiceScope scope = _host.Scope();
        return await SystemSettings.GetAsync(scope.ServiceProvider.GetRequiredService<MatMailDbContext>(), key);
    }

    [DbFact]
    public async Task A_started_installation_knows_its_id_and_the_versions_of_program_and_files()
    {
        await StartupInitializer.MigrateDatabaseAsync(_host.Services, NullLogger.Instance);

        string? id = await SettingAsync(SystemSettings.InstallationId);
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("1", await SettingAsync(SystemSettings.DataVersion));
        Assert.Equal(AppInfo.Version, await SettingAsync(SystemSettings.AppVersion));

        // the id stays when the program starts again
        await StartupInitializer.MigrateDatabaseAsync(_host.Services, NullLogger.Instance);
        Assert.Equal(id, await SettingAsync(SystemSettings.InstallationId));
    }

    [DbFact]
    public async Task A_database_that_a_newer_version_upgraded_is_not_touched_and_not_retried()
    {
        await ExecuteAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromTheFuture', '99.0.0')");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<IncompatibleVersionException>(() => StartupInitializer.MigrateDatabaseAsync(_host.Services, NullLogger.Instance));

        Assert.Contains("29990101000000_FromTheFuture", refused.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}: it was retried although a newer database does not get older");
    }

    [DbFact]
    public async Task Files_of_a_newer_layout_are_not_touched_either()
    {
        await StartupInitializer.MigrateDatabaseAsync(_host.Services, NullLogger.Instance);
        await ExecuteAsync("UPDATE \"SystemSetting\" SET \"Value\" = '99' WHERE \"Key\" = 'DataVersion'");

        var refused = await Assert.ThrowsAsync<IncompatibleVersionException>(() => StartupInitializer.MigrateDatabaseAsync(_host.Services, NullLogger.Instance));

        Assert.Contains("layout 99", refused.Message);
    }
}

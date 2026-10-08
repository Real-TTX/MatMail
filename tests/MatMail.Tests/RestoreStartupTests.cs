using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace MatMail.Tests;

public class PendingRestoreTests
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "matmail-pending-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_request_is_kept_in_the_data_volume_until_it_is_cleared()
    {
        Assert.Null(PendingRestore.Read(_dataDir));

        var request = new PendingRestore { BackupPath = "restore/incoming/a.zip", DeleteBackupAfterwards = true, SafetyBackup = false, HoldOutboundQueue = false, RequestedBy = "alice", RequestedUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc) };
        request.Save(_dataDir);

        PendingRestore read = PendingRestore.Read(_dataDir)!;
        Assert.Equal("restore/incoming/a.zip", read.BackupPath);
        Assert.True(read.DeleteBackupAfterwards);
        Assert.False(read.SafetyBackup);
        Assert.False(read.HoldOutboundQueue);
        Assert.Equal("alice", read.RequestedBy);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dataDir, "restore", "incoming", "a.zip")), read.ResolvedBackupPath(_dataDir));

        PendingRestore.Clear(_dataDir);
        Assert.Null(PendingRestore.Read(_dataDir));
        PendingRestore.Clear(_dataDir);   // nothing to clear is no error
        Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void The_passphrase_is_not_written_in_plain_text()
    {
        IDataProtector protector = new EphemeralDataProtectionProvider().CreateProtector(PendingRestore.Purpose);
        var request = new PendingRestore { BackupPath = "a.zip", ProtectedPassphrase = PendingRestore.Protect(protector, "correct horse battery staple") };
        request.Save(_dataDir);

        string file = File.ReadAllText(PendingRestore.MarkerPath(_dataDir));
        Assert.DoesNotContain("correct horse", file);
        Assert.Equal("correct horse battery staple", PendingRestore.Read(_dataDir)!.Passphrase(protector));
        Assert.Null(new PendingRestore().Passphrase(protector));
        Assert.Null(PendingRestore.Protect(protector, null));
        Assert.Null(PendingRestore.Protect(protector, string.Empty));
        Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void A_request_that_cannot_be_read_is_none()
    {
        Directory.CreateDirectory(PendingRestore.Folder(_dataDir));
        File.WriteAllText(PendingRestore.MarkerPath(_dataDir), "{ this is not json");

        Assert.Null(PendingRestore.Read(_dataDir));
        Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void The_report_of_a_restore_is_kept_for_the_backup_page()
    {
        Assert.Null(RestoreReport.Read(_dataDir));

        new RestoreReport { Succeeded = false, Message = "The passphrase is wrong.", FinishedUtc = DateTime.UtcNow, SafetyBackup = "matmail-pre-restore-x.zip" }.Save(_dataDir);
        RestoreReport read = RestoreReport.Read(_dataDir)!;
        Assert.False(read.Succeeded);
        Assert.Equal("The passphrase is wrong.", read.Message);
        Assert.Equal("matmail-pre-restore-x.zip", read.SafetyBackup);

        RestoreReport.Clear(_dataDir);
        Assert.Null(RestoreReport.Read(_dataDir));
        Directory.Delete(_dataDir, recursive: true);
    }
}

public class RestoreProgressHostTests
{
    [Fact]
    public async Task The_progress_page_answers_while_a_restore_runs_and_shows_how_it_ended()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "matmail-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var config = new AppConfig { Server = { WebPort = RestoreStartupTests.FreePort(), WebHttps = false }, Tls = { GenerateSelfSigned = false } };

        RestoreProgressHost? page = await RestoreProgressHost.StartAsync(config, dataDir, NullLogger.Instance);
        Assert.NotNull(page);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{config.Server.WebPort}") };

            using (JsonDocument first = JsonDocument.Parse(await client.GetStringAsync("/restore-status")))
            {
                Assert.Equal("running", first.RootElement.GetProperty("state").GetString());
            }

            page.Report(new BackupProgress("database", "MailMessage", 7, 30, 1234));
            using (JsonDocument progress = JsonDocument.Parse(await client.GetStringAsync("/restore-status")))
            {
                Assert.Equal("database", progress.RootElement.GetProperty("stage").GetString());
                Assert.Equal("MailMessage", progress.RootElement.GetProperty("item").GetString());
                Assert.Equal(7, progress.RootElement.GetProperty("done").GetInt32());
                Assert.Equal(30, progress.RootElement.GetProperty("total").GetInt32());
            }

            // any other address shows the page (and says "come back later"), the container health check is told that all is well
            using HttpResponseMessage anything = await client.GetAsync("/Mail?folder=Inbox");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, anything.StatusCode);
            Assert.Contains("restore-status", await anything.Content.ReadAsStringAsync());
            Assert.Equal("text/html", anything.Content.Headers.ContentType!.MediaType);
            using HttpResponseMessage health = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Contains("restoring", await health.Content.ReadAsStringAsync());

            page.Finish(false, "no space left");
            using JsonDocument last = JsonDocument.Parse(await client.GetStringAsync("/restore-status"));
            Assert.Equal("failed", last.RootElement.GetProperty("state").GetString());
            Assert.Equal("no space left", last.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await page.DisposeAsync();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}

/// <summary>What happens at start-up: a restore that the web interface asked for, one from the environment, and the commands of the command line.</summary>
public class RestoreStartupTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-startup-" + Guid.NewGuid().ToString("N"));
    private readonly List<TestHost> _hosts = [];
    private TestHost _host = null!;

    private string DataDir => Path.Combine(_root, "data");

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        _host = await NewHostAsync();
        await _host.SeedAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (TestHost host in _hosts)
        {
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<TestHost> NewHostAsync()
    {
        TestHost host = await TestHost.CreateAsync(config =>
        {
            // the progress page of a restore must not meet the ports of anything else on the machine
            config.Server.WebPort = FreePort();
            config.Server.WebHttps = false;
            config.Tls.GenerateSelfSigned = false;
        });
        _hosts.Add(host);
        return host;
    }

    // ---------------------------------------------------------------------------------------------
    // A request of the web interface
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_request_of_the_web_interface_is_carried_out_before_the_application_starts()
    {
        string upload = await MakeBackupAsync(Path.Combine(PendingRestore.IncomingFolder(DataDir), "upload.zip"));
        File.WriteAllText(Path.Combine(DataDir, "marker.txt"), "before");
        await ExecuteAsync(_host, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since the backup'");

        new PendingRestore { BackupPath = upload, DeleteBackupAfterwards = true, RequestedBy = "alice", RequestedUtc = DateTime.UtcNow }.Save(DataDir);
        StartupOutcome outcome = await RestoreStartup.RunAsync([], _host.Config, DataDir, NullLogger.Instance);

        Assert.Null(outcome.ExitCode);
        Assert.True(outcome.Restored);
        Assert.Equal("Home", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));
        Assert.Null(PendingRestore.Read(DataDir));
        Assert.False(File.Exists(upload));   // the uploaded copy is removed

        RestoreReport report = RestoreReport.Read(DataDir)!;
        Assert.True(report.Succeeded, report.Message);
        Assert.Equal("alice", report.RequestedBy);
        Assert.NotNull(report.BackupCreatedUtc);
        Assert.Equal(AppInfo.Version, report.BackupVersion);

        // what was there before is kept, so that a restore of the wrong backup can be undone
        string safety = Assert.Single(Directory.GetFiles(Path.Combine(DataDir, "backups"), "matmail-pre-restore-*.zip"));
        Assert.Equal(report.SafetyBackup, Path.GetFileName(safety));
        using BackupArchive archive = BackupArchive.Open(safety);
        await archive.VerifyAsync();
    }

    [DbFact]
    public async Task A_wrong_passphrase_stops_the_request_and_changes_nothing()
    {
        string plain = await MakeBackupAsync(Path.Combine(_root, "plain.zip"));
        string encrypted = Path.Combine(PendingRestore.IncomingFolder(DataDir), "upload.mmbak");
        Directory.CreateDirectory(Path.GetDirectoryName(encrypted)!);
        await BackupFiles.EncryptAsync(plain, encrypted, "right passphrase");
        await ExecuteAsync(_host, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since the backup'");

        IDataProtector protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DataDir, "keys")), b => b.SetApplicationName("MatMail")).CreateProtector(PendingRestore.Purpose);
        new PendingRestore { BackupPath = encrypted, ProtectedPassphrase = PendingRestore.Protect(protector, "wrong passphrase"), DeleteBackupAfterwards = true }.Save(DataDir);

        StartupOutcome outcome = await RestoreStartup.RunAsync([], _host.Config, DataDir, NullLogger.Instance);

        Assert.Null(outcome.ExitCode);
        Assert.False(outcome.Restored);
        Assert.Equal("Changed since the backup", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));
        Assert.Null(PendingRestore.Read(DataDir));   // not tried again at the next start
        RestoreReport report = RestoreReport.Read(DataDir)!;
        Assert.False(report.Succeeded);
        Assert.Contains("passphrase", report.Message);
        Assert.False(Directory.Exists(Path.Combine(DataDir, "backups")) && Directory.GetFiles(Path.Combine(DataDir, "backups")).Length > 0);
    }

    [DbFact]
    public async Task An_encrypted_backup_is_restored_with_the_passphrase_of_the_request()
    {
        string plain = await MakeBackupAsync(Path.Combine(_root, "plain.zip"));
        string encrypted = Path.Combine(_root, "backup.mmbak");
        await BackupFiles.EncryptAsync(plain, encrypted, "right passphrase");
        await ExecuteAsync(_host, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since the backup'");

        IDataProtector protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DataDir, "keys")), b => b.SetApplicationName("MatMail")).CreateProtector(PendingRestore.Purpose);
        new PendingRestore { BackupPath = encrypted, ProtectedPassphrase = PendingRestore.Protect(protector, "right passphrase"), SafetyBackup = true }.Save(DataDir);

        StartupOutcome outcome = await RestoreStartup.RunAsync([], _host.Config, DataDir, NullLogger.Instance);

        Assert.True(outcome.Restored, RestoreReport.Read(DataDir)?.Message);
        Assert.Equal("Home", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));
        Assert.True(File.Exists(encrypted));   // not an upload: it stays
        // the state before is protected like the backup that replaces it
        Assert.True(BackupFiles.IsEncrypted(Assert.Single(Directory.GetFiles(Path.Combine(DataDir, "backups"), "matmail-pre-restore-*.mmbak"))));
    }

    // ---------------------------------------------------------------------------------------------
    // The environment: a new server with the data of the old one
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_environment_restores_into_an_empty_installation_and_only_into_that()
    {
        string backup = await MakeBackupAsync(Path.Combine(_root, "from-the-old-server.zip"));
        TestHost fresh = await NewHostAsync();   // migrated, but nobody has signed up yet
        string freshData = Path.Combine(_root, "fresh-data");
        Directory.CreateDirectory(freshData);

        Environment.SetEnvironmentVariable(RestoreStartup.RestoreFromVariable, backup);
        try
        {
            StartupOutcome first = await RestoreStartup.RunAsync([], fresh.Config, freshData, NullLogger.Instance);
            Assert.True(first.Restored, RestoreReport.Read(freshData)?.Message);
            Assert.Equal(new[] { "alice", "bob" }, await UsersAsync(fresh));
            Assert.Empty(Directory.Exists(Path.Combine(freshData, "backups")) ? Directory.GetFiles(Path.Combine(freshData, "backups")) : Array.Empty<string>());   // nothing to save in an empty installation

            // the variable stays in the configuration of the server: at the next start there is data, and it is left alone
            await ExecuteAsync(fresh, "UPDATE \"Tenant\" SET \"Name\" = 'Changed after the restore'");
            StartupOutcome second = await RestoreStartup.RunAsync([], fresh.Config, freshData, NullLogger.Instance);
            Assert.False(second.Restored);
            Assert.Equal("Changed after the restore", await ScalarAsync<string>(fresh, "SELECT \"Name\" FROM \"Tenant\""));

            // a file that is not there stops nothing
            Environment.SetEnvironmentVariable(RestoreStartup.RestoreFromVariable, Path.Combine(_root, "missing.zip"));
            Assert.False((await RestoreStartup.RunAsync([], fresh.Config, freshData, NullLogger.Instance)).Restored);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RestoreStartup.RestoreFromVariable, null);
        }
    }

    [DbFact]
    public async Task The_environment_gives_the_passphrase_of_an_encrypted_backup()
    {
        string plain = await MakeBackupAsync(Path.Combine(_root, "plain.zip"));
        string encrypted = Path.Combine(_root, "from-the-old-server.mmbak");
        await BackupFiles.EncryptAsync(plain, encrypted, "right passphrase");
        TestHost fresh = await NewHostAsync();
        string freshData = Path.Combine(_root, "fresh-data");
        Directory.CreateDirectory(freshData);

        Environment.SetEnvironmentVariable(RestoreStartup.RestoreFromVariable, encrypted);
        try
        {
            Environment.SetEnvironmentVariable(RestoreStartup.RestorePassphraseVariable, "wrong passphrase");
            Assert.False((await RestoreStartup.RunAsync([], fresh.Config, freshData, NullLogger.Instance)).Restored);
            Assert.Empty(await UsersAsync(fresh));

            Environment.SetEnvironmentVariable(RestoreStartup.RestorePassphraseVariable, "right passphrase");
            Assert.True((await RestoreStartup.RunAsync([], fresh.Config, freshData, NullLogger.Instance)).Restored);
            Assert.Equal(new[] { "alice", "bob" }, await UsersAsync(fresh));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RestoreStartup.RestoreFromVariable, null);
            Environment.SetEnvironmentVariable(RestoreStartup.RestorePassphraseVariable, null);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Command line
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_command_line_makes_a_backup_into_a_folder_or_a_file()
    {
        string folder = Path.Combine(_root, "nas");
        StartupOutcome toFolder = await RestoreStartup.RunAsync(["--backup", folder + Path.DirectorySeparatorChar], _host.Config, DataDir, NullLogger.Instance);
        Assert.Equal(0, toFolder.ExitCode);
        string made = Assert.Single(Directory.GetFiles(folder));
        Assert.True(BackupFiles.IsBackupFileName(Path.GetFileName(made)));
        using (BackupArchive archive = BackupArchive.Open(made))
        {
            await archive.VerifyAsync();
        }

        string named = Path.Combine(_root, "named.zip");
        Assert.Equal(0, (await RestoreStartup.RunAsync(["--backup", named], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.True(File.Exists(named));

        // an existing file is not overwritten
        Assert.Equal(1, (await RestoreStartup.RunAsync(["--backup", named], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.Equal(64, (await RestoreStartup.RunAsync(["--backup"], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
    }

    [DbFact]
    public async Task The_command_line_encrypts_with_a_passphrase_from_the_environment()
    {
        string path = Path.Combine(_root, "secret.mmbak");
        Environment.SetEnvironmentVariable("MATMAIL_TEST_PASSPHRASE", "correct horse");
        try
        {
            Assert.Equal(0, (await RestoreStartup.RunAsync(["--backup", path, "--passphrase-env", "MATMAIL_TEST_PASSPHRASE"], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
            Assert.True(BackupFiles.IsEncrypted(path));
            using BackupArchive archive = BackupArchive.Open(path, "correct horse");
            Assert.True(archive.Encrypted);

            // a variable that is not set is an error, not a backup without a passphrase
            Assert.Equal(64, (await RestoreStartup.RunAsync(["--backup", Path.Combine(_root, "x.mmbak"), "--passphrase-env", "MATMAIL_TEST_NOT_SET"], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
            Assert.False(File.Exists(Path.Combine(_root, "x.mmbak")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MATMAIL_TEST_PASSPHRASE", null);
        }
    }

    [DbFact]
    public async Task The_command_line_restores_a_stopped_installation_and_refuses_a_running_one()
    {
        string backup = await MakeBackupAsync(Path.Combine(_root, "backup.zip"));
        await ExecuteAsync(_host, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since the backup'");

        // somebody else is connected to the database (the running application): not without --force
        await using var other = new NpgsqlConnection(_host.ConnectionString);
        await other.OpenAsync();
        Assert.Equal(75, (await RestoreStartup.RunAsync(["--restore", backup], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.Equal("Changed since the backup", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));

        await other.CloseAsync();
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(0, (await RestoreStartup.RunAsync(["--restore", backup], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.Equal("Home", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));
        Assert.Single(Directory.GetFiles(Path.Combine(DataDir, "backups"), "matmail-pre-restore-*"));   // what was there is saved

        Assert.Equal(66, (await RestoreStartup.RunAsync(["--restore", Path.Combine(_root, "missing.zip")], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.Equal(64, (await RestoreStartup.RunAsync(["--restore"], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
    }

    [DbFact]
    public async Task A_damaged_backup_is_refused_by_the_command_line_without_a_change()
    {
        string backup = await MakeBackupAsync(Path.Combine(_root, "backup.zip"));
        await ExecuteAsync(_host, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since the backup'");
        using (var file = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetLength(file.Length - 500);
        }

        NpgsqlConnection.ClearAllPools();
        Assert.Equal(1, (await RestoreStartup.RunAsync(["--restore", backup, "--no-safety-backup"], _host.Config, DataDir, NullLogger.Instance)).ExitCode);
        Assert.Equal("Changed since the backup", await ScalarAsync<string>(_host, "SELECT \"Name\" FROM \"Tenant\""));
        Assert.False(RestoreReport.Read(DataDir)!.Succeeded);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<string> MakeBackupAsync(string path)
    {
        await BackupJob.CreateFileAsync(new BackupSource(_host.ConnectionString, DataDir), path, passphrase: null, Path.Combine(DataDir, "tmp"));
        return path;
    }

    private static async Task ExecuteAsync(TestHost host, string sql)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TestHost host, string sql)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> UsersAsync(TestHost host)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        return await db.Users.IgnoreQueryFilters().Select(u => u.LoginName).OrderBy(n => n).ToArrayAsync();
    }
}

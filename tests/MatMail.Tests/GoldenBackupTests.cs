using MatMail.Backup;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Npgsql;

namespace MatMail.Tests;

/// <summary>What the golden backups hold (<c>Fixtures/Backups</c>).</summary>
public static class GoldenFiles
{
    public const string Passphrase = "Golden-Passphrase-1";

    /// <summary>Bytes that can be made again without a copy: the test needs to know what a file in the backup has to be.</summary>
    public static byte[] Blob(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 % 251)).ToArray();
}

/// <summary>
/// Backups that an <b>older</b> program wrote, kept as files (<c>Fixtures/Backups/backup-format-1.zip</c> and <c>.mmbak</c>: the first
/// version of the format, the schema up to <c>AddBackupPlans</c>, file layout 1, rows in every one of its 29 tables, so that a later
/// migration has to work on data in each of them – a NOT NULL column without a default only fails on a table that has rows). They
/// are the only proof that what an old program wrote can still be restored: the other tests write their backups with the current
/// code, which would change the writer and the reader together without anyone noticing.
/// <para>
/// <b>Never make these files again.</b> A change of the format, of the layout of the files or a new migration needs nothing here:
/// the restore has to bring them up to date. A change of the format (<see cref="BackupFormat.Version"/> 2) adds a second pair next to
/// them, and these keep having to restore.
/// </para>
/// </summary>
public class GoldenBackupTests : IAsyncLifetime
{
    private const string SchemaOfTheFiles = "20261008225122_AddBackupPlans";
    private const string InstallationOfTheFiles = "78f6da1c-bb6e-44bc-9fae-02c2ee77bbbf";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-golden-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;

    private string DataDir => Path.Combine(_root, "data");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        _host = await TestHost.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Backups", name);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    [DbTheory]
    [InlineData("backup-format-1.zip", null)]
    [InlineData("backup-format-1.mmbak", GoldenFiles.Passphrase)]
    public async Task A_backup_of_the_first_version_still_restores_and_is_brought_up_to_date(string file, string? passphrase)
    {
        using (BackupArchive archive = BackupArchive.Open(Fixture(file), passphrase))
        {
            await archive.VerifyAsync();
            BackupInfo info = archive.Describe();
            Assert.Equal(file.EndsWith(".mmbak", StringComparison.Ordinal), info.Encrypted);
            Assert.Equal(1, info.Format);
            Assert.Equal(SchemaOfTheFiles, info.SchemaVersion);
            Assert.Equal(1, info.DataVersion);
            Assert.Equal(InstallationOfTheFiles, info.InstallationId);
            Assert.Equal(29, info.Tables);
            Assert.All(archive.Manifest.Database.Tables, t => Assert.True(t.Rows > 0, $"{t.Name} has no rows: the file was made again, or made wrongly"));
            Assert.Equal(4, info.Files);

            RestoreResult result = await BackupRestorer.RestoreAsync(
                archive, new RestoreOptions { ConnectionString = _host.ConnectionString, DataDir = DataDir }, NullLogger.Instance);

            Assert.Equal(SchemaOfTheFiles, result.FromSchema);
            Assert.Equal(info.Rows, result.Rows);
            Assert.Equal(4, result.Files);
        }

        // the schema is the one of this program, whatever it has become since the files were made
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations().ToArray(), (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        }

        // the rows
        Assert.Equal("Home", await ScalarAsync<string>("SELECT \"Name\" FROM \"Tenant\""));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM \"User\""));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM \"MailMessage\""));   // one for alice, one for alice and one for bob
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM \"Signature\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"MailTemplate\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"RelayRule\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"MailAccount\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"BackupPlan\""));
        Assert.Equal("NAS", await ScalarAsync<string>("SELECT \"Name\" FROM \"BackupTarget\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"BackupRun\""));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM \"OutboundMessage\""));
        Assert.Equal(InstallationOfTheFiles, await ScalarAsync<string>("SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = 'InstallationId'"));

        // the people can sign in with what they had
        using (IServiceScope scope = _host.Scope())
        {
            var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
            Assert.True((await signIn.ValidateCredentialsAsync("alice", "Test-Passw0rd!")).Succeeded);
            Assert.False((await signIn.ValidateCredentialsAsync("alice", "Another-Passw0rd!")).Succeeded);
        }

        // the mail with its attachment, byte for byte
        byte[] raw = await ScalarAsync<byte[]>(
            "SELECT c.\"Raw\" FROM \"MailMessageContent\" c JOIN \"MailMessage\" m ON m.\"Id\" = c.\"MessageId\" WHERE m.\"Subject\" = 'With a file' ORDER BY m.\"Id\" LIMIT 1");
        using (var stream = new MemoryStream(raw))
        {
            MimeMessage message = await MimeMessage.LoadAsync(stream);
            MimePart attachment = message.Attachments.OfType<MimePart>().Single();
            Assert.NotNull(attachment.Content);
            using var content = new MemoryStream();
            await attachment.Content!.DecodeToAsync(content);
            Assert.Equal(GoldenFiles.Blob(20_000), content.ToArray());
        }

        // the files; the settings that say how this machine is deployed stay with this machine
        Assert.Equal("<key id='1'/>", File.ReadAllText(Path.Combine(DataDir, "keys", "key-1.xml")));
        Assert.Equal("-----BEGIN CERTIFICATE-----", File.ReadAllText(Path.Combine(DataDir, "certs", "server.pem")));
        Assert.Equal(GoldenFiles.Blob(5_000), File.ReadAllBytes(Path.Combine(DataDir, "deep", "nested", "blob.bin")));
        string settings = File.ReadAllText(Path.Combine(DataDir, "config", "app.json"));
        Assert.Contains("de-DE", settings);
        Assert.DoesNotContain("\"Host\": \"db\"", settings);   // the Database section is not taken from a backup

        Assert.Equal("1", await ScalarAsync<string>("SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = 'DataVersion'"));
    }
}

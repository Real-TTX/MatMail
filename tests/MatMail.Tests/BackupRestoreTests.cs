using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using MatMail.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Npgsql;

namespace MatMail.Tests;

/// <summary>Backups of a real database and data volume, and what a restore does with them.</summary>
public class BackupRestoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-backup-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _databases = [];
    private readonly List<TestHost> _hosts = [];
    private TestHost _host = null!;
    private Seed _seed = null!;

    private string DataDir => Path.Combine(_root, "data");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (TestHost other in _hosts)
        {
            await other.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        foreach (string database in _databases)
        {
            try
            {
                await RunAdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            }
            catch (NpgsqlException)
            {
                // best effort, like the test host
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The round trip
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_backup_brings_back_the_database_and_the_files_as_they_were()
    {
        byte[] attachment = RandomNumberGenerator.GetBytes(300_000);
        await PopulateAsync(attachment);
        Write("config/app.json", """{ "Database": { "Host": "live-db" }, "Display": { "Culture": "de-DE" } }""");
        Write("keys/key-1.xml", "<key id='1'/>");
        Write("certs/server.pem", "-----BEGIN CERTIFICATE-----");
        Write("deep/nested/blob.bin", attachment);
        Write("tmp/scratch.txt", "scratch");
        Write("backups/old.zip", "an earlier backup");
        Write("nas/elsewhere.zip", "a backup folder somewhere else in the volume");

        // The folders differ afterwards in the number that makes IMAP clients load them again, the settings in what restoring noted
        // down, the queue in what is held (all checked below); every other table is as it was.
        var ignored = new Dictionary<string, string> { ["MailFolder"] = "UidValidity" };
        var before = await DigestsAsync(_host.ConnectionString, ignored, "SystemSetting", "OutboundMessage");
        int tableCount = (await DigestsAsync(_host.ConnectionString)).Count;
        var folderValidity = await FolderValidityAsync(_host.ConnectionString);
        string path = await BackupAsync(_host, DataDir, partBytes: 64 * 1024, excluded: [Path.Combine(DataDir, "nas")]);

        BackupInfo info;
        using (BackupArchive archive = BackupArchive.Open(path))
        {
            await archive.VerifyAsync();
            info = archive.Describe();

            Assert.False(info.Encrypted);
            Assert.Equal(AppInfo.Version, info.AppVersion);
            Assert.Equal(tableCount, info.Tables);
            Assert.Equal(new[] { "certs/server.pem", "config/app.json", "deep/nested/blob.bin", "keys/key-1.xml" }, archive.Manifest.Files.Select(f => f.Path).Order().ToArray());
            Assert.Equal("PostgreSQL", archive.Manifest.Database.Provider);
            Assert.Equal(archive.Manifest.Database.AppliedMigrations.Last(), archive.Manifest.Database.SchemaVersion);
            Assert.False(string.IsNullOrEmpty(archive.Manifest.Database.ServerVersion));

            // the big table is cut into parts that are read as one
            ManifestTable contents = archive.Manifest.Database.Tables.Single(t => t.Name == "MailMessageContent");
            Assert.True(contents.Parts.Count > 3, $"{contents.Parts.Count} parts");
        }

        // Everything changes after the backup: rows, files, a new file in a folder the backup holds, a new folder.
        await ExecuteAsync(_host.ConnectionString, "TRUNCATE \"MailMessage\" CASCADE; UPDATE \"Tenant\" SET \"Name\" = 'Changed'; DELETE FROM \"Domain\"");
        File.Delete(Path.Combine(DataDir, "keys", "key-1.xml"));
        Write("keys/new.xml", "<key id='2'/>");
        Write("certs/server.pem", "changed");
        Write("extra/other.txt", "not in the backup");
        Assert.NotEqual(before["MailMessage"], (await DigestsAsync(_host.ConnectionString, ignored, "SystemSetting", "OutboundMessage"))["MailMessage"]);

        RestoreResult result = await RestoreAsync(path, _host, DataDir);

        Assert.Equal(info.Tables, result.Tables);
        Assert.Equal(info.Rows, result.Rows);
        Assert.Equal(4, result.Files);
        Assert.Equal(result.FromSchema, result.ToSchema);
        Assert.Equal(before, await DigestsAsync(_host.ConnectionString, ignored, "SystemSetting", "OutboundMessage"));

        Assert.Equal(attachment, File.ReadAllBytes(Path.Combine(DataDir, "deep", "nested", "blob.bin")));
        Assert.Equal("<key id='1'/>", File.ReadAllText(Path.Combine(DataDir, "keys", "key-1.xml")));
        Assert.Equal("-----BEGIN CERTIFICATE-----", File.ReadAllText(Path.Combine(DataDir, "certs", "server.pem")));
        Assert.False(File.Exists(Path.Combine(DataDir, "keys", "new.xml")));                  // the folder was replaced as a whole
        Assert.Equal("not in the backup", File.ReadAllText(Path.Combine(DataDir, "extra", "other.txt")));   // a folder the backup does not hold stays
        Assert.Equal("scratch", File.ReadAllText(Path.Combine(DataDir, "tmp", "scratch.txt")));
        Assert.Equal("an earlier backup", File.ReadAllText(Path.Combine(DataDir, "backups", "old.zip")));
        Assert.Equal("a backup folder somewhere else in the volume", File.ReadAllText(Path.Combine(DataDir, "nas", "elsewhere.zip")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(DataDir, "tmp"), "restore-*"));

        // the IMAP clients are told that the numbers of the messages may have changed
        var validityAfter = await FolderValidityAsync(_host.ConnectionString);
        Assert.Equal(folderValidity.Keys.Order(), validityAfter.Keys.Order());
        Assert.All(validityAfter, f => Assert.True(f.Value > folderValidity[f.Key], $"folder {f.Key}"));

        // the database knows what it was restored from and which version of the files it has
        Assert.NotNull(await ScalarAsync<string?>(_host.ConnectionString, "SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = 'LastRestore'"));
        Assert.Equal("1", await ScalarAsync<string?>(_host.ConnectionString, "SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = 'DataVersion'"));
    }

    [DbFact]
    public async Task Ids_continue_where_they_were_so_none_is_given_twice()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(1000));
        long messageHighest = await ScalarAsync<long>(_host.ConnectionString, "SELECT max(\"Id\") FROM \"MailMessage\"");

        // an id that was handed out and is gone again by the time of the backup is not handed out a second time either
        long highest;
        using (IServiceScope scope = _host.Scope())
        {
            var tenants = scope.ServiceProvider.GetRequiredService<TenantService>();
            (Tenant? extra, string? error) = await tenants.CreateAsync("Short lived", null);
            Assert.Null(error);
            highest = extra!.Id;
        }

        await ExecuteAsync(_host.ConnectionString, $"DELETE FROM \"Tenant\" WHERE \"Id\" = {highest}");
        Assert.True(await ScalarAsync<long>(_host.ConnectionString, "SELECT max(\"Id\") FROM \"Tenant\"") < highest);

        string path = await BackupAsync(_host, DataDir);
        await RestoreAsync(path, _host, DataDir);

        using IServiceScope after = _host.Scope();
        var restoredTenants = after.ServiceProvider.GetRequiredService<TenantService>();
        (Tenant? created, string? createError) = await restoredTenants.CreateAsync("After the restore", null);
        Assert.Null(createError);
        Assert.True(created!.Id > highest, $"{created.Id} must come after {highest}");

        var delivery = after.ServiceProvider.GetRequiredService<MailDelivery>();
        await delivery.DeliverAsync(RawMail.Build("max@sender.test", "alice@example.test", "New", "x"), new DeliverySource { EnvelopeRecipients = ["alice@example.test"] });
        Assert.True(await ScalarAsync<long>(_host.ConnectionString, "SELECT max(\"Id\") FROM \"MailMessage\"") > messageHighest);
    }

    [DbFact]
    public async Task Mail_that_was_queued_for_sending_is_held_so_that_nothing_is_sent_twice()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(100));
        string path = await BackupAsync(_host, DataDir);

        // pending and sending at the time of the backup, sent since: the backup cannot know
        await ExecuteAsync(_host.ConnectionString, "UPDATE \"OutboundMessage\" SET \"Status\" = 'Sent' WHERE \"Status\" = 'Pending'");
        await RestoreAsync(path, _host, DataDir);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        List<OutboundMessage> queue = await db.OutboundMessages.AsNoTracking().OrderBy(o => o.Id).ToListAsync();

        Assert.Equal(2, queue.Count);
        Assert.Equal(OutboundStatus.Failed, queue[0].Status);
        Assert.Contains("Restored from a backup", queue[0].LastError);
        Assert.Equal(OutboundStatus.Sent, queue[1].Status);
    }

    [DbFact]
    public async Task The_queue_can_be_left_as_it_was_when_asked_to()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(100));
        string path = await BackupAsync(_host, DataDir);

        using (BackupArchive archive = BackupArchive.Open(path))
        {
            await BackupRestorer.RestoreAsync(archive, Options(_host, DataDir, hold: false), NullLogger.Instance);
        }

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal(OutboundStatus.Pending, (await db.OutboundMessages.AsNoTracking().OrderBy(o => o.Id).FirstAsync()).Status);
    }

    [DbFact]
    public async Task The_settings_file_keeps_how_this_server_is_deployed()
    {
        Write("config/app.json", """
            { "Database": { "Host": "old-db", "Password": "old" },
              "Server": { "Hostname": "mail.old.example", "WebPort": 1111, "WebHttps": false },
              "Display": { "Culture": "de-DE", "TimeZone": "Europe/Vienna" },
              "Smtp": { "Port": 2525 } }
            """);
        string path = await BackupAsync(_host, DataDir);

        Write("config/app.json", """
            { "Database": { "Host": "live-db", "Password": "live" },
              "Server": { "Hostname": "mail.live.example", "WebPort": 9933, "WebHttps": true },
              "Display": { "Culture": "en-US" } }
            """);
        await RestoreAsync(path, _host, DataDir);

        JsonNode restored = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "config", "app.json")))!;
        Assert.Equal("live-db", (string?)restored["Database"]!["Host"]);
        Assert.Equal("live", (string?)restored["Database"]!["Password"]);
        Assert.Equal("mail.live.example", (string?)restored["Server"]!["Hostname"]);
        Assert.Equal(9933, (int?)restored["Server"]!["WebPort"]);
        Assert.True((bool?)restored["Server"]!["WebHttps"]);
        Assert.Equal("de-DE", (string?)restored["Display"]!["Culture"]);
        Assert.Equal("Europe/Vienna", (string?)restored["Display"]!["TimeZone"]);
        Assert.Equal(2525, (int?)restored["Smtp"]!["Port"]);
    }

    [DbFact]
    public async Task A_backup_can_be_restored_into_an_empty_installation()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(50_000));
        Write("keys/key-1.xml", "<key id='1'/>");
        Write("config/app.json", """{ "Display": { "Culture": "de-DE" } }""");
        string path = await BackupAsync(_host, DataDir);

        // the new installation has its own data (which the restore replaces) and its own database connection
        TestHost fresh = await NewHostAsync();
        await fresh.SeedAsync();
        string freshData = Path.Combine(_root, "fresh-data");
        Write("config/app.json", """{ "Database": { "Host": "new-db" } }""", freshData);
        Write("keys/generated-at-first-start.xml", "<key id='9'/>", freshData);

        await RestoreAsync(path, fresh, freshData);

        var ignored = new Dictionary<string, string> { ["MailFolder"] = "UidValidity" };
        Assert.Equal(
            await DigestsAsync(_host.ConnectionString, ignored, "SystemSetting", "OutboundMessage"),
            await DigestsAsync(fresh.ConnectionString, ignored, "SystemSetting", "OutboundMessage"));
        Assert.Equal("<key id='1'/>", File.ReadAllText(Path.Combine(freshData, "keys", "key-1.xml")));
        Assert.Equal("new-db", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(freshData, "config", "app.json")))!["Database"]!["Host"]);
        Assert.Equal("de-DE", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(freshData, "config", "app.json")))!["Display"]!["Culture"]);

        // the installation works with what it got: the people of the backup can be found by the application
        using IServiceScope scope = fresh.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal(new[] { "alice", "bob" }, await db.Users.IgnoreQueryFilters().Select(u => u.LoginName).OrderBy(n => n).ToArrayAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [DbFact]
    public async Task A_backup_taken_while_mail_arrives_restores_cleanly()
    {
        using var stop = new CancellationTokenSource();
        Task arriving = Task.Run(async () =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                using IServiceScope scope = _host.Scope();
                var delivery = scope.ServiceProvider.GetRequiredService<MailDelivery>();
                await delivery.DeliverAsync(RawMail.Build("max@sender.test", "alice@example.test", $"Mail {i}", new string('x', 4_000)), new DeliverySource { EnvelopeRecipients = ["alice@example.test", "bob@example.test"] });
            }
        });

        var backups = new List<string>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(200);
                backups.Add(await BackupAsync(_host, DataDir, name: $"during-{i}.zip"));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await arriving;
        }

        // A snapshot that is not of one moment would show as a message without its folder or its text: the keys are checked on loading.
        TestHost other = await NewHostAsync();
        string otherData = Path.Combine(_root, "other-data");
        Directory.CreateDirectory(otherData);
        long previous = -1;
        foreach (string path in backups)
        {
            await RestoreAsync(path, other, otherData);
            long messages = await ScalarAsync<long>(other.ConnectionString, "SELECT count(*) FROM \"MailMessage\"");
            Assert.True(messages >= previous, "later backups hold at least as much mail as earlier ones");
            previous = messages;
        }

        Assert.True(previous > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Encryption
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task An_encrypted_backup_needs_its_passphrase_and_restores_with_it()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(20_000));
        string plain = await BackupAsync(_host, DataDir);
        string encrypted = Path.Combine(_root, "out", "backup.mmbak");
        await BackupFiles.EncryptAsync(plain, encrypted, "correct horse battery staple");
        var before = await DigestsAsync(_host.ConnectionString, new Dictionary<string, string> { ["MailFolder"] = "UidValidity" }, "SystemSetting", "OutboundMessage");

        Assert.True(BackupFiles.IsEncrypted(encrypted));
        Assert.False(BackupFiles.IsEncrypted(plain));
        Assert.Throws<BackupPassphraseException>(() => BackupArchive.Open(encrypted));
        Assert.Throws<BackupPassphraseException>(() => BackupArchive.Open(encrypted, "wrong"));

        await ExecuteAsync(_host.ConnectionString, "TRUNCATE \"MailMessage\" CASCADE");
        using (BackupArchive archive = BackupArchive.Open(encrypted, "correct horse battery staple"))
        {
            Assert.True(archive.Encrypted);
            Assert.True(archive.Describe().Encrypted);
            await archive.VerifyAsync();
        }

        await RestoreAsync(encrypted, _host, DataDir, passphrase: "correct horse battery staple");
        Assert.Equal(before, await DigestsAsync(_host.ConnectionString, new Dictionary<string, string> { ["MailFolder"] = "UidValidity" }, "SystemSetting", "OutboundMessage"));
    }

    [DbFact]
    public async Task A_changed_encrypted_backup_is_refused_without_touching_anything()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(2_500_000));   // several chunks of the encrypted file
        string plain = await BackupAsync(_host, DataDir);
        string encrypted = Path.Combine(_root, "out", "backup.mmbak");
        await BackupFiles.EncryptAsync(plain, encrypted, "secret");

        using (var file = new FileStream(encrypted, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.True(file.Length > 3 * BackupEncryption.DefaultChunkBytes);
            file.Position = file.Length / 2;
            int value = file.ReadByte();
            file.Position = file.Length / 2;
            file.WriteByte((byte)(value ^ 0x10));
        }

        var before = await DigestsAsync(_host.ConnectionString);
        await Assert.ThrowsAsync<BackupCorruptException>(async () =>
        {
            using BackupArchive archive = BackupArchive.Open(encrypted, "secret");
            await archive.VerifyAsync();
        });

        await Assert.ThrowsAsync<BackupCorruptException>(() => RestoreAsync(encrypted, _host, DataDir, passphrase: "secret"));
        Assert.Equal(before, await DigestsAsync(_host.ConnectionString));
    }

    // ---------------------------------------------------------------------------------------------
    // Backups that cannot be restored: the restore stops and everything stays as it was
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_damaged_part_stops_the_restore_and_leaves_everything_as_it_was()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(200_000));
        Write("keys/key-1.xml", "<key id='1'/>");
        string path = await BackupAsync(_host, DataDir);
        string biggest;
        using (BackupArchive archive = BackupArchive.Open(path))
        {
            biggest = archive.Manifest.Database.Tables.SelectMany(t => t.Parts).OrderByDescending(p => p.Bytes).First().Entry;
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = zip.GetEntry(biggest)!;
            var content = new MemoryStream();
            using (Stream read = entry.Open())
            {
                read.CopyTo(content);
            }

            byte[] bytes = content.ToArray();
            bytes[bytes.Length / 2] ^= 0xFF;
            entry.Delete();
            using Stream write = zip.CreateEntry(biggest).Open();
            write.Write(bytes);
        }

        // something changed since the backup: that must still be what is there afterwards
        await ExecuteAsync(_host.ConnectionString, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since'");
        File.WriteAllText(Path.Combine(DataDir, "keys", "key-1.xml"), "changed since");
        var before = await DigestsAsync(_host.ConnectionString);

        using (BackupArchive archive = BackupArchive.Open(path))
        {
            await Assert.ThrowsAsync<BackupCorruptException>(() => archive.VerifyAsync());
        }

        await Assert.ThrowsAsync<BackupCorruptException>(() => RestoreAsync(path, _host, DataDir));

        Assert.Equal(before, await DigestsAsync(_host.ConnectionString));
        Assert.Equal("changed since", File.ReadAllText(Path.Combine(DataDir, "keys", "key-1.xml")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(DataDir, "tmp"), "restore-*"));
    }

    [DbFact]
    public async Task A_backup_that_is_cut_off_is_no_backup()
    {
        await PopulateAsync(RandomNumberGenerator.GetBytes(10_000));
        string path = await BackupAsync(_host, DataDir);

        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetLength(file.Length - 300);
        }

        Assert.Throws<BackupCorruptException>(() => BackupArchive.Open(path));

        string notAZip = Path.Combine(_root, "out", "not-a-backup.zip");
        File.WriteAllText(notAZip, "this is just text");
        Assert.Throws<BackupCorruptException>(() => BackupArchive.Open(notAZip));
    }

    [DbTheory]
    [InlineData("schema")]
    [InlineData("files")]
    [InlineData("format")]
    public async Task A_backup_of_a_newer_version_is_refused(string what)
    {
        string path = await BackupAsync(_host, DataDir);
        RewriteManifest(path, m =>
        {
            switch (what)
            {
                case "schema":
                    m.Database.SchemaVersion = "29990101000000_FromTheFuture";
                    m.Database.AppliedMigrations.Add(m.Database.SchemaVersion);
                    break;
                case "files":
                    m.Database.DataVersion = 99;
                    break;
                default:
                    m.Format = 99;
                    break;
            }
        });
        var before = await DigestsAsync(_host.ConnectionString);

        await Assert.ThrowsAsync<BackupIncompatibleException>(() => RestoreAsync(path, _host, DataDir));
        Assert.Equal(before, await DigestsAsync(_host.ConnectionString));
    }

    [DbFact]
    public async Task A_backup_cannot_write_outside_the_data_volume()
    {
        Write("keys/key-1.xml", "<key/>");
        string path = await BackupAsync(_host, DataDir);
        byte[] evil = "evil"u8.ToArray();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using (Stream stream = zip.CreateEntry("data/../evil.txt").Open())
            {
                stream.Write(evil);
            }

            using (Stream stream = zip.CreateEntry("data/tmp/inside-scratch.txt").Open())
            {
                stream.Write(evil);
            }
        }

        string sha = Convert.ToHexStringLower(SHA256.HashData(evil));
        RewriteManifest(path, m => m.Files.Add(new ManifestFile { Path = "../evil.txt", Bytes = evil.Length, Sha256 = sha, ModifiedUtc = DateTime.UtcNow }));
        await Assert.ThrowsAsync<BackupCorruptException>(() => RestoreAsync(path, _host, DataDir));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(DataDir, "evil.txt")));

        // the scratch folder and the backups are not what a backup replaces, whatever it lists
        RewriteManifest(path, m =>
        {
            m.Files.RemoveAll(f => f.Path == "../evil.txt");
            m.Files.Add(new ManifestFile { Path = "tmp/inside-scratch.txt", Bytes = evil.Length, Sha256 = sha, ModifiedUtc = DateTime.UtcNow });
        });
        await RestoreAsync(path, _host, DataDir);
        Assert.False(File.Exists(Path.Combine(DataDir, "tmp", "inside-scratch.txt")));
    }

    // ---------------------------------------------------------------------------------------------
    // Versions: backups of older programs
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_backup_of_every_earlier_database_version_restores_to_the_current_one()
    {
        string[] migrations;
        using (IServiceScope scope = _host.Scope())
        {
            migrations = scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.GetMigrations().ToArray();
        }

        Assert.True(migrations.Length >= 9);
        foreach (string migration in migrations)
        {
            string oldConnection = await CreateOldDatabaseAsync(migration);
            string source = Path.Combine(_root, "old-" + migration);
            Write("config/app.json", "{}", source);
            Write("keys/key-1.xml", "<key/>", source);
            string path = Path.Combine(_root, "out", $"old-{migration}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                BackupManifest manifest = await BackupWriter.WriteAsync(new BackupSource(oldConnection, source), file);
                Assert.Equal(migration, manifest.Database.SchemaVersion);
                Assert.Equal(1, manifest.Database.DataVersion);   // an installation that predates the version numbers
                Assert.Null(manifest.InstallationId);
            }

            RestoreResult result = await RestoreAsync(path, _host, DataDir);

            Assert.Equal(migration, result.FromSchema);
            Assert.Equal(migrations.Last(), result.ToSchema);
            using IServiceScope after = _host.Scope();
            var db = after.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(migrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(0, await db.Users.IgnoreQueryFilters().CountAsync());
        }
    }

    [DbFact]
    public async Task The_data_of_an_old_version_is_migrated_like_in_an_upgrade()
    {
        // The version of 7 October 2026: administrators had neither the permission for the branding nor the one for security.
        string oldConnection = await CreateOldDatabaseAsync("20261007220311_AddUserProfileFields");
        long tenant = await InsertAsync(oldConnection, "Tenant", new() { ["Name"] = "'Old Inc'", ["IsActive"] = "true" });
        await InsertAsync(oldConnection, "Role", new() { ["TenantId"] = tenant.ToString(), ["Name"] = "'Administrator'", ["IsBuiltIn"] = "true", ["Permissions"] = "ARRAY['mail.use','users.manage']::text[]" });
        await InsertAsync(oldConnection, "Role", new() { ["TenantId"] = tenant.ToString(), ["Name"] = "'User'", ["IsBuiltIn"] = "true", ["Permissions"] = "ARRAY['mail.use']::text[]" });

        string source = Path.Combine(_root, "old-data");
        Write("config/app.json", "{}", source);
        string path = Path.Combine(_root, "out", "old.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            await BackupWriter.WriteAsync(new BackupSource(oldConnection, source), file);
        }

        await RestoreAsync(path, _host, DataDir);

        Assert.Equal("Old Inc", await ScalarAsync<string?>(_host.ConnectionString, "SELECT \"Name\" FROM \"Tenant\""));
        string[] administrator = await ScalarAsync<string[]>(_host.ConnectionString, "SELECT \"Permissions\" FROM \"Role\" WHERE \"Name\" = 'Administrator'");
        string[] user = await ScalarAsync<string[]>(_host.ConnectionString, "SELECT \"Permissions\" FROM \"Role\" WHERE \"Name\" = 'User'");
        Assert.Contains("mail.use", administrator);
        Assert.Contains("branding.manage", administrator);
        Assert.Contains("security.manage", administrator);
        Assert.Equal(new[] { "mail.use" }, user);
    }

    [DbFact]
    public async Task The_files_of_an_old_version_are_migrated_before_they_are_put_in_place()
    {
        Write("keys/old-name.txt", "key");
        Write("config/app.json", "{}");
        await ExecuteAsync(_host.ConnectionString, "UPDATE \"Tenant\" SET \"Description\" = 'before'");
        string path = await BackupAsync(_host, DataDir);   // layout 1

        var live = new List<string>();
        DataMigration step = new(2, "rename the key file and mark the tenants", async context =>
        {
            live.Add(context.DataDir);
            File.Move(Path.Combine(context.DataDir, "keys", "old-name.txt"), Path.Combine(context.DataDir, "keys", "new-name.txt"));
            await context.ExecuteAsync("UPDATE \"Tenant\" SET \"Description\" = 'migrated'");
        });

        // while the migration runs, the live files are not touched: it works on the restored copy
        File.WriteAllText(Path.Combine(DataDir, "keys", "old-name.txt"), "live key");
        using (BackupArchive archive = BackupArchive.Open(path))
        {
            RestoreResult result = await BackupRestorer.RestoreAsync(archive, Options(_host, DataDir, steps: [step]), NullLogger.Instance);
            Assert.Equal(1, result.FromDataVersion);
            Assert.Equal(2, result.ToDataVersion);
        }

        Assert.Single(live);
        Assert.NotEqual(Path.GetFullPath(DataDir), Path.GetFullPath(live[0]));
        Assert.False(File.Exists(Path.Combine(DataDir, "keys", "old-name.txt")));
        Assert.Equal("key", File.ReadAllText(Path.Combine(DataDir, "keys", "new-name.txt")));
        Assert.Equal("migrated", await ScalarAsync<string?>(_host.ConnectionString, "SELECT \"Description\" FROM \"Tenant\""));
        Assert.Equal("2", await ScalarAsync<string?>(_host.ConnectionString, "SELECT \"Value\" FROM \"SystemSetting\" WHERE \"Key\" = 'DataVersion'"));
    }

    [DbFact]
    public async Task A_step_that_fails_stops_the_restore_and_leaves_everything_as_it_was()
    {
        Write("keys/key-1.xml", "key");
        string path = await BackupAsync(_host, DataDir);
        await ExecuteAsync(_host.ConnectionString, "UPDATE \"Tenant\" SET \"Name\" = 'Changed since'");
        File.WriteAllText(Path.Combine(DataDir, "keys", "key-1.xml"), "changed since");
        var before = await DigestsAsync(_host.ConnectionString);

        DataMigration broken = new(2, "breaks", _ => throw new InvalidOperationException("this step does not work"));
        using (BackupArchive archive = BackupArchive.Open(path))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupRestorer.RestoreAsync(archive, Options(_host, DataDir, steps: [broken]), NullLogger.Instance));
            Assert.Contains("does not work", failure.Message);
        }

        Assert.Equal(before, await DigestsAsync(_host.ConnectionString));
        Assert.Equal("changed since", File.ReadAllText(Path.Combine(DataDir, "keys", "key-1.xml")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(DataDir, "tmp"), "restore-*"));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task PopulateAsync(byte[] attachment)
    {
        using IServiceScope scope = _host.Scope();
        var delivery = scope.ServiceProvider.GetRequiredService<MailDelivery>();
        await delivery.DeliverAsync(RawMail.Build("max@sender.test", "alice@example.test", "Hello", "plain"), new DeliverySource { EnvelopeRecipients = ["alice@example.test"] });
        await delivery.DeliverAsync(MailWithAttachment(attachment), new DeliverySource { EnvelopeRecipients = ["alice@example.test", "bob@example.test"] });

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.OutboundMessages.Add(new OutboundMessage
        {
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            EnvelopeFrom = "alice@example.test",
            Recipients = ["out@elsewhere.test"],
            Subject = "Waiting",
            Raw = RawMail.Build("alice@example.test", "out@elsewhere.test", "Waiting", "x"),
            SizeBytes = 100,
            Status = OutboundStatus.Pending,
            NextAttemptDate = DateTime.UtcNow,
        });
        db.OutboundMessages.Add(new OutboundMessage
        {
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            EnvelopeFrom = "alice@example.test",
            Recipients = ["out@elsewhere.test"],
            Subject = "Gone",
            Raw = RawMail.Build("alice@example.test", "out@elsewhere.test", "Gone", "x"),
            SizeBytes = 100,
            Status = OutboundStatus.Sent,
            SentDate = DateTime.UtcNow,
            NextAttemptDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static byte[] MailWithAttachment(byte[] attachment)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("max@sender.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        message.Subject = "With a file";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        var body = new BodyBuilder { TextBody = "The file is attached." };
        body.Attachments.Add("random.bin", attachment);
        message.Body = body.ToMessageBody();

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private void Write(string relative, string text, string? dataDir = null) => Write(relative, System.Text.Encoding.UTF8.GetBytes(text), dataDir);

    private void Write(string relative, byte[] content, string? dataDir = null)
    {
        string path = Path.Combine(dataDir ?? DataDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private async Task<string> BackupAsync(TestHost host, string dataDir, string name = "backup.zip", long? partBytes = null, string[]? excluded = null)
    {
        string path = Path.Combine(_root, "out", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        await BackupWriter.WriteAsync(
            new BackupSource(host.ConnectionString, dataDir) { PartBytes = partBytes ?? BackupFormat.PartBytes, ExcludedDirectories = excluded ?? [] },
            file);
        return path;
    }

    private static RestoreOptions Options(TestHost host, string dataDir, IReadOnlyList<DataMigration>? steps = null, bool hold = true)
        => new() { ConnectionString = host.ConnectionString, DataDir = dataDir, DataMigrationSteps = steps, HoldOutboundQueue = hold };

    private static async Task<RestoreResult> RestoreAsync(string path, TestHost host, string dataDir, string? passphrase = null)
    {
        using BackupArchive archive = BackupArchive.Open(path, passphrase);
        return await BackupRestorer.RestoreAsync(archive, Options(host, dataDir), NullLogger.Instance);
    }

    private async Task<TestHost> NewHostAsync()
    {
        TestHost host = await TestHost.CreateAsync();
        _hosts.Add(host);
        return host;
    }

    private static void RewriteManifest(string path, Action<BackupManifest> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        ZipArchiveEntry entry = zip.GetEntry(BackupFormat.ManifestEntry)!;
        BackupManifest manifest;
        using (Stream read = entry.Open())
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(read, BackupFormat.Json)!;
        }

        change(manifest);
        entry.Delete();
        using Stream write = zip.CreateEntry(BackupFormat.ManifestEntry).Open();
        JsonSerializer.Serialize(write, manifest, BackupFormat.Json);
    }

    /// <summary>One digest per table: the number of rows and a hash of all of them, independent of their order.</summary>
    private static Task<Dictionary<string, string>> DigestsAsync(string connectionString, params string[] skip)
        => DigestsAsync(connectionString, [], skip);

    private static async Task<Dictionary<string, string>> DigestsAsync(string connectionString, Dictionary<string, string> ignoredColumns, params string[] skip)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename", connection))
        await using (NpgsqlDataReader reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables.Where(t => !skip.Contains(t)))
        {
            string row = ignoredColumns.TryGetValue(table, out string? column) ? $"(to_jsonb(t) - '{column}')" : "to_jsonb(t)";
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || COALESCE(md5(string_agg({row}::text, E'\\n' ORDER BY {row}::text)), '') FROM \"{table}\" t",
                connection);
            digests[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return digests;
    }

    private static async Task<Dictionary<long, long>> FolderValidityAsync(string connectionString)
    {
        var validity = new Dictionary<long, long>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"Id\", \"UidValidity\" FROM \"MailFolder\"", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            validity[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        return validity;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task RunAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A database as the program of an earlier version left it: migrated up to <paramref name="migration"/> and no further.</summary>
    private async Task<string> CreateOldDatabaseAsync(string migration)
    {
        string name = "matmail_old_" + Guid.NewGuid().ToString("N")[..10];
        await RunAdminAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);

        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.AdminConnectionString) { Database = name };
        using var db = new MatMailDbContext(new DbContextOptionsBuilder<MatMailDbContext>().UseNpgsql(builder.ConnectionString).Options, new CurrentUser());
        await db.Database.GetService<IMigrator>().MigrateAsync(migration);
        return builder.ConnectionString;
    }

    /// <summary>Inserts a row into a table of any version of the schema: what is not given and has no default gets a neutral value. Returns the new id.</summary>
    private static async Task<long> InsertAsync(string connectionString, string table, Dictionary<string, string> values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var columns = new List<(string Name, string Type)>();
        await using (var describe = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO' ORDER BY ordinal_position",
            connection))
        {
            describe.Parameters.AddWithValue("table", table);
            await using NpgsqlDataReader reader = await describe.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var all = new Dictionary<string, string>(values);
        foreach ((string name, string type) in columns.Where(c => !values.ContainsKey(c.Name)))
        {
            all[name] = type switch
            {
                "boolean" => "false",
                "bigint" or "integer" or "smallint" or "numeric" or "double precision" => "0",
                "timestamp with time zone" or "timestamp without time zone" => "now()",
                "uuid" => "gen_random_uuid()",
                "bytea" => "'\\x'",
                "jsonb" or "json" => "'{}'",
                "ARRAY" => "'{}'",
                _ => "''",
            };
        }

        string sql = $"INSERT INTO \"{table}\" ({string.Join(", ", all.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", all.Values)}) RETURNING \"Id\"";
        await using var insert = new NpgsqlCommand(sql, connection);
        return (long)(await insert.ExecuteScalarAsync())!;
    }
}

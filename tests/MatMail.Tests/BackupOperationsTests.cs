using System.IO.Compression;
using System.Text.Json;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The first half of a restore (what the running program does) and the backups that are made for download.</summary>
public class BackupOperationsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-operations-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;

    private string DataDir => Path.Combine(_root, "data");
    private string Folder => Path.Combine(_root, "target");
    private RestorePreparation Preparation => _host.Services.GetRequiredService<RestorePreparation>();
    private BackupDownloads Downloads => _host.Services.GetRequiredService<BackupDownloads>();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "config"));
        File.WriteAllText(Path.Combine(DataDir, "config", "app.json"), "{}");
        _host = await TestHost.CreateAsync(config => config.DataDir = DataDir);
        await _host.SeedAsync();
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

    // ---------------------------------------------------------------------------------------------
    // Preparing a restore
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_backup_in_a_folder_of_the_server_is_prepared_in_place_and_the_request_is_left()
    {
        (long target, string name) = await MakeBackupInTargetAsync();

        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, null, SafetyBackup: true, HoldOutboundQueue: false, VerifyFirst: true, "alice")));
        PreparationStatus status = await WaitForAsync(s => s.State != PreparationState.Running);

        Assert.Equal(PreparationState.Restarting, status.State);
        PendingRestore request = PendingRestore.Read(DataDir)!;
        Assert.Equal(Path.Combine(Folder, name), request.BackupPath);
        Assert.False(request.DeleteBackupAfterwards);   // a file of a target is not ours to remove
        Assert.True(request.SafetyBackup);
        Assert.False(request.HoldOutboundQueue);
        Assert.Equal("alice", request.RequestedBy);
        Assert.Null(request.ProtectedPassphrase);

        // it is going to restart: nothing else may start meanwhile
        Assert.False(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, true, "bob")));
    }

    [DbFact]
    public async Task An_encrypted_backup_needs_its_passphrase_before_anything_is_stopped()
    {
        (long target, string name) = await MakeBackupInTargetAsync(passphrase: "correct horse battery staple");

        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, true, "alice")));
        PreparationStatus missing = await WaitForAsync(s => s.State != PreparationState.Running);
        Assert.Equal(PreparationState.Failed, missing.State);
        Assert.Contains("passphrase", missing.Message);
        Assert.Null(PendingRestore.Read(DataDir));   // nothing was left, nothing stops

        Preparation.Reset();
        Assert.Equal(PreparationState.Idle, Preparation.Status.State);
        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, "wrong", true, true, true, "alice")));
        Assert.Contains("passphrase", (await WaitForAsync(s => s.State != PreparationState.Running)).Message);

        Preparation.Reset();
        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, "correct horse battery staple", true, true, true, "alice")));
        Assert.Equal(PreparationState.Restarting, (await WaitForAsync(s => s.State != PreparationState.Running)).State);

        // the passphrase waits in the request, but not in the clear
        PendingRestore request = PendingRestore.Read(DataDir)!;
        Assert.DoesNotContain("correct horse", File.ReadAllText(PendingRestore.MarkerPath(DataDir)));
        IDataProtector protector = _host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(PendingRestore.Purpose);
        Assert.Equal("correct horse battery staple", request.Passphrase(protector));
    }

    [DbFact]
    public async Task A_backup_of_a_newer_version_is_refused_before_anything_is_stopped()
    {
        (long target, string name) = await MakeBackupInTargetAsync();
        RewriteManifest(Path.Combine(Folder, name), m =>
        {
            m.Database.SchemaVersion = "29990101000000_FromTheFuture";
            m.Database.AppliedMigrations.Add(m.Database.SchemaVersion);
        });

        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, false, "alice")));
        PreparationStatus status = await WaitForAsync(s => s.State != PreparationState.Running);

        Assert.Equal(PreparationState.Failed, status.State);
        Assert.Contains("newer", status.Message);
        Assert.Null(PendingRestore.Read(DataDir));
    }

    [DbFact]
    public async Task A_damaged_backup_is_found_by_the_check_and_without_it_only_by_the_restore()
    {
        (long target, string name) = await MakeBackupInTargetAsync();
        string path = Path.Combine(Folder, name);
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

        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, VerifyFirst: true, "alice")));
        PreparationStatus checkedFirst = await WaitForAsync(s => s.State != PreparationState.Running);
        Assert.Equal(PreparationState.Failed, checkedFirst.State);
        Assert.Null(PendingRestore.Read(DataDir));

        // the check can be switched off: the restore itself then meets the damage (and changes nothing)
        Preparation.Reset();
        Assert.True(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, VerifyFirst: false, "alice")));
        Assert.Equal(PreparationState.Restarting, (await WaitForAsync(s => s.State != PreparationState.Running)).State);
    }

    [DbFact]
    public async Task A_file_or_a_target_that_is_gone_is_said_so()
    {
        (long target, string name) = await MakeBackupInTargetAsync();

        Assert.True(Preparation.Start(new RestoreRequest(target, "matmail-aaaaaa-p1-20200101-000000-1.0.zip", null, null, true, true, true, "alice")));
        PreparationStatus noFile = await WaitForAsync(s => s.State != PreparationState.Running);
        Assert.Equal(PreparationState.Failed, noFile.State);
        Assert.Contains("any more", noFile.Message);

        Preparation.Reset();
        Assert.True(Preparation.Start(new RestoreRequest(target + 1000, name, null, null, true, true, true, "alice")));
        Assert.Contains("target", (await WaitForAsync(s => s.State != PreparationState.Running)).Message);

        Preparation.Reset();
        Assert.True(Preparation.Start(new RestoreRequest(null, null, null, null, true, true, true, "alice")));
        Assert.Contains("No backup", (await WaitForAsync(s => s.State != PreparationState.Running)).Message);
    }

    [DbFact]
    public async Task While_a_backup_runs_a_restore_cannot_be_prepared()
    {
        (long target, string name) = await MakeBackupInTargetAsync();
        var coordinator = _host.Services.GetRequiredService<BackupCoordinator>();

        using (coordinator.TryBegin("backup", CancellationToken.None))
        {
            Assert.False(Preparation.Start(new RestoreRequest(target, name, null, null, true, true, true, "alice")));
        }

        Assert.Equal(PreparationState.Idle, Preparation.Status.State);
    }

    // ---------------------------------------------------------------------------------------------
    // Uploads
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task An_uploaded_backup_waits_until_it_is_restored_or_discarded()
    {
        (_, string name) = await MakeBackupInTargetAsync();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Folder, name));

        (Guid id, long size, bool encrypted) = await Preparation.SaveUploadAsync(new MemoryStream(bytes), "my backup.zip", CancellationToken.None);

        Assert.Equal(bytes.Length, size);
        Assert.False(encrypted);
        string path = Preparation.FindUpload(id)!;
        Assert.StartsWith(PendingRestore.IncomingFolder(DataDir), path);
        Assert.EndsWith(".zip", path);
        Assert.Empty(Directory.GetFiles(PendingRestore.IncomingFolder(DataDir), "*.partial"));

        Preparation.DiscardUpload(id);
        Assert.Null(Preparation.FindUpload(id));

        (Guid again, _, _) = await Preparation.SaveUploadAsync(new MemoryStream(bytes), "my backup.zip", CancellationToken.None);
        Assert.True(Preparation.Start(new RestoreRequest(null, null, again, null, true, true, true, "alice")));
        Assert.Equal(PreparationState.Restarting, (await WaitForAsync(s => s.State != PreparationState.Running)).State);
        PendingRestore request = PendingRestore.Read(DataDir)!;
        Assert.Equal(Preparation.FindUpload(again), request.BackupPath);
        Assert.True(request.DeleteBackupAfterwards);   // an upload is removed once it was restored

        // while a restore is under way nothing is cleaned up
        Assert.Equal(0, Preparation.CleanUp(TimeSpan.Zero));
        Assert.NotNull(Preparation.FindUpload(again));
    }

    [DbFact]
    public async Task Something_that_is_no_backup_is_not_kept_and_encrypted_ones_are_recognised()
    {
        await Assert.ThrowsAsync<BackupCorruptException>(() => Preparation.SaveUploadAsync(new MemoryStream("just some text"u8.ToArray()), "notes.zip", CancellationToken.None));
        await Assert.ThrowsAsync<BackupCorruptException>(() => Preparation.SaveUploadAsync(new MemoryStream([1, 2]), "tiny.zip", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(PendingRestore.IncomingFolder(DataDir)));

        (_, string name) = await MakeBackupInTargetAsync(passphrase: "secret phrase");
        (Guid id, _, bool encrypted) = await Preparation.SaveUploadAsync(File.OpenRead(Path.Combine(Folder, name)), name, CancellationToken.None);
        Assert.True(encrypted);
        Assert.EndsWith(".mmbak", Preparation.FindUpload(id));
    }

    [DbFact]
    public async Task Uploads_that_nobody_went_on_with_are_cleaned_up_after_a_day()
    {
        (_, string name) = await MakeBackupInTargetAsync();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Folder, name));
        (Guid id, _, _) = await Preparation.SaveUploadAsync(new MemoryStream(bytes), "backup.zip", CancellationToken.None);

        Assert.Equal(0, Preparation.CleanUp(TimeSpan.FromHours(24)));
        Assert.NotNull(Preparation.FindUpload(id));

        File.SetLastWriteTimeUtc(Preparation.FindUpload(id)!, DateTime.UtcNow.AddDays(-2));
        Assert.Equal(1, Preparation.CleanUp(TimeSpan.FromHours(24)));
        Assert.Null(Preparation.FindUpload(id));
    }

    // ---------------------------------------------------------------------------------------------
    // A backup to download
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_backup_for_download_is_made_in_the_background_and_removed_after_a_while()
    {
        Guid token = Downloads.Start(passphrase: null)!.Value;
        DownloadStatus status = await WaitForDownloadAsync(token);

        Assert.Equal(JobState.Ready, status.State);
        Assert.False(status.Encrypted);
        (string path, string name) = Downloads.Ready(token)!.Value;
        Assert.True(BackupFiles.IsBackupFileName(name));
        Assert.Equal(BackupFiles.ManualLabel, BackupFiles.Parse(name)!.Label);
        Assert.Equal(new FileInfo(path).Length, status.Bytes);
        using (BackupArchive archive = BackupArchive.Open(path))
        {
            await archive.VerifyAsync();
            Assert.Contains(archive.Manifest.Files, f => f.Path == "config/app.json");
            Assert.DoesNotContain(archive.Manifest.Files, f => f.Path.StartsWith("tmp/", StringComparison.Ordinal));   // the file itself is in the scratch folder
        }

        Assert.Equal(0, Downloads.CleanUp(TimeSpan.FromHours(1)));   // not yet
        Assert.True(File.Exists(path));
        Assert.Equal(1, Downloads.CleanUp(TimeSpan.Zero));
        Assert.False(File.Exists(path));
        Assert.Null(Downloads.Ready(token));
    }

    [DbFact]
    public async Task A_backup_for_download_can_be_encrypted_and_only_one_runs_at_a_time()
    {
        var coordinator = _host.Services.GetRequiredService<BackupCoordinator>();
        using (coordinator.TryBegin("backup", CancellationToken.None))
        {
            Assert.Null(Downloads.Start(null));
        }

        Guid token = Downloads.Start("correct horse battery staple")!.Value;
        DownloadStatus status = await WaitForDownloadAsync(token);

        Assert.Equal(JobState.Ready, status.State);
        Assert.True(status.Encrypted);
        (string path, string name) = Downloads.Ready(token)!.Value;
        Assert.EndsWith(".mmbak", name);
        Assert.True(BackupFiles.IsEncrypted(path));
        using (BackupArchive archive = BackupArchive.Open(path, "correct horse battery staple"))
        {
            await archive.VerifyAsync();
        }

        Downloads.Remove(token);
        Assert.False(File.Exists(path));
        Assert.Null(Downloads.Get(token));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<(long TargetId, string FileName)> MakeBackupInTargetAsync(string? passphrase = null)
    {
        long targetId;
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var target = new BackupTarget { Name = "Disk " + Guid.NewGuid().ToString("N")[..6], Kind = BackupTargetKind.Local, Path = Folder };
            db.BackupTargets.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        string name = BackupFiles.Name(DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 5000)), AppInfo.Version, passphrase is not null, "p1", "abcdef");
        await BackupJob.CreateFileAsync(new BackupSource(_host.ConnectionString, DataDir), Path.Combine(Folder, name), passphrase, Path.Combine(DataDir, "tmp"));
        return (targetId, name);
    }

    private async Task<PreparationStatus> WaitForAsync(Func<PreparationStatus, bool> done)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < giveUp)
        {
            PreparationStatus status = Preparation.Status;
            if (done(status))
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The preparation did not finish.");
    }

    private async Task<DownloadStatus> WaitForDownloadAsync(Guid token)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < giveUp)
        {
            DownloadStatus? status = Downloads.Get(token);
            if (status is { State: not JobState.Running })
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The backup did not finish.");
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
}

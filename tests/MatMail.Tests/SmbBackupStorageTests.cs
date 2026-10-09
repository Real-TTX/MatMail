using System.Security.Cryptography;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The SMB client against a real share (see <see cref="TestSmb"/>).</summary>
public class SmbBackupStorageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "matmail-smb-" + Guid.NewGuid().ToString("N"));

    public SmbBackupStorageTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LocalFile(int bytes, string? name = null)
    {
        string path = Path.Combine(_scratch, name ?? Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(bytes));
        return path;
    }

    private static string BackupName(string label = "p1", int seconds = 0)
        => BackupFiles.Name(new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc).AddSeconds(seconds), "1.0", false, label, "abcdef");

    [SmbFact]
    public async Task A_check_connects_and_proves_that_files_can_be_written_without_leaving_one()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());

        StorageCheck check = await storage.CheckAsync(CancellationToken.None);

        Assert.True(check.Ok, check.Message);
        Assert.Contains(TestSmb.Share, check.Message);
        Assert.Empty(await storage.ListAsync(CancellationToken.None));
    }

    [SmbFact]
    public async Task A_wrong_password_is_said_so()
    {
        var storage = new SmbBackupStorage(TestSmb.Options(password: "not-the-password"));

        StorageCheck check = await storage.CheckAsync(CancellationToken.None);

        Assert.False(check.Ok);
        Assert.Contains("password", check.Message);
    }

    [SmbFact]
    public async Task A_share_that_does_not_exist_is_said_so()
    {
        var storage = new SmbBackupStorage(TestSmb.Options(share: "no-such-share"));

        StorageCheck check = await storage.CheckAsync(CancellationToken.None);

        Assert.False(check.Ok);
        Assert.Contains("no-such-share", check.Message);
    }

    [SmbFact]
    public async Task A_server_that_is_not_there_is_said_so()
    {
        var storage = new SmbBackupStorage(TestSmb.Options(host: "no-such-server.invalid"));

        StorageCheck check = await storage.CheckAsync(CancellationToken.None);

        Assert.False(check.Ok);
        Assert.Contains("no-such-server.invalid", check.Message);
    }

    [SmbFact]
    public async Task A_file_goes_up_and_comes_back_unchanged()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());
        string local = LocalFile(12 * 1024 * 1024 + 123);   // several chunks, and a last one that is not full
        string name = BackupName();
        var reported = new List<long>();

        await storage.UploadAsync(local, name, new Progress<long>(reported.Add), CancellationToken.None);

        IReadOnlyList<RemoteBackupFile> files = await storage.ListAsync(CancellationToken.None);
        RemoteBackupFile remote = Assert.Single(files);   // the .partial file is gone
        Assert.Equal(name, remote.Name);
        Assert.Equal(new FileInfo(local).Length, remote.Bytes);
        Assert.True(Math.Abs((DateTime.UtcNow - remote.ModifiedUtc).TotalHours) < 24 + 14, "a time that is of the right day");

        string back = Path.Combine(_scratch, "back.bin");
        var downloaded = new List<long>();
        await storage.DownloadAsync(name, back, new Progress<long>(downloaded.Add), CancellationToken.None);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(local)), SHA256.HashData(File.ReadAllBytes(back)));

        await Task.Delay(100);   // Progress<T> posts to the thread pool
        Assert.Equal(new FileInfo(local).Length, reported.Max());
        Assert.Equal(new FileInfo(local).Length, downloaded.Max());

        await storage.DeleteAsync(name, CancellationToken.None);
        Assert.Empty(await storage.ListAsync(CancellationToken.None));
        await storage.DeleteAsync(name, CancellationToken.None);   // gone already: no error
    }

    [SmbFact]
    public async Task An_empty_file_and_a_small_one_work_too()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());

        await storage.UploadAsync(LocalFile(0), BackupName("p1", 1), null, CancellationToken.None);
        await storage.UploadAsync(LocalFile(1), BackupName("p1", 2), null, CancellationToken.None);

        IReadOnlyList<RemoteBackupFile> files = await storage.ListAsync(CancellationToken.None);
        Assert.Equal(new long[] { 0, 1 }, files.OrderBy(f => f.Name).Select(f => f.Bytes).ToArray());
    }

    [SmbFact]
    public async Task A_file_is_never_replaced()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());
        string name = BackupName();
        string first = LocalFile(1000);
        await storage.UploadAsync(first, name, null, CancellationToken.None);

        var exists = await Assert.ThrowsAsync<BackupStorageException>(() => storage.UploadAsync(LocalFile(2000), name, null, CancellationToken.None));

        Assert.Contains("exists already", exists.Message);
        Assert.Equal(1000, Assert.Single(await storage.ListAsync(CancellationToken.None)).Bytes);
    }

    [SmbFact]
    public async Task Folders_are_made_as_needed_and_are_kept_apart()
    {
        string root = "matmail-test/" + Guid.NewGuid().ToString("N")[..10];
        var deep = new SmbBackupStorage(TestSmb.Options(root + "/a/b/c"));
        var other = new SmbBackupStorage(TestSmb.Options(root + "/other\\folder"));   // either slash

        Assert.Empty(await deep.ListAsync(CancellationToken.None));   // not there yet: nothing in it, no error
        await deep.UploadAsync(LocalFile(100), BackupName(), null, CancellationToken.None);
        await other.UploadAsync(LocalFile(200), BackupName(), null, CancellationToken.None);

        Assert.Equal(100, Assert.Single(await deep.ListAsync(CancellationToken.None)).Bytes);
        Assert.Equal(200, Assert.Single(await other.ListAsync(CancellationToken.None)).Bytes);
    }

    [SmbFact]
    public async Task A_transfer_that_is_stopped_leaves_nothing_behind()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());
        string local = LocalFile(20 * 1024 * 1024);
        using var cancel = new CancellationTokenSource();
        var stopAfterTheFirstChunk = new Progress<long>(_ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.UploadAsync(local, BackupName(), stopAfterTheFirstChunk, cancel.Token));

        Assert.Empty(await storage.ListAsync(CancellationToken.None));   // neither the file nor its .partial
    }

    [SmbFact]
    public async Task What_is_not_a_plain_name_is_refused_before_anything_happens()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());

        await Assert.ThrowsAsync<BackupStorageException>(() => storage.UploadAsync(LocalFile(10), "../escape.zip", null, CancellationToken.None));
        await Assert.ThrowsAsync<BackupStorageException>(() => storage.DownloadAsync("sub\\file.zip", Path.Combine(_scratch, "x"), null, CancellationToken.None));
        await Assert.ThrowsAsync<BackupStorageException>(() => storage.DeleteAsync("..", CancellationToken.None));
    }

    [SmbFact]
    public async Task A_file_that_is_not_there_is_said_so()
    {
        var storage = new SmbBackupStorage(TestSmb.Options());

        var missing = await Assert.ThrowsAsync<BackupStorageException>(() => storage.DownloadAsync(BackupName(), Path.Combine(_scratch, "x"), null, CancellationToken.None));

        Assert.Contains("not on the share", missing.Message);
    }

    [SmbFact]
    public async Task The_shares_of_a_server_can_be_listed()
    {
        IReadOnlyList<string> shares = await SmbBackupStorage.ListSharesAsync(TestSmb.Host, TestSmb.Domain, TestSmb.User, TestSmb.Password);

        Assert.Contains(TestSmb.Share, shares);
        Assert.DoesNotContain(shares, s => s.EndsWith('$'));
    }
}

/// <summary>A plan that writes to a share, end to end.</summary>
public class SmbBackupPlanTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-smbplan-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;

    private string DataDir => Path.Combine(_root, "data");

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

    [SmbDbFact]
    public async Task A_plan_writes_its_backups_to_the_share_and_keeps_only_as_many_as_it_should()
    {
        SmbTargetOptions options = TestSmb.Options();
        long planId;
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var target = new BackupTarget
            {
                Name = "NAS",
                Kind = BackupTargetKind.Smb,
                Host = options.Host,
                Share = options.Share,
                Path = options.Folder,
                Domain = options.Domain,
                Username = options.Username,
                PasswordProtected = scope.ServiceProvider.GetRequiredService<MatMail.Services.SecretProtector>().Protect(options.Password!),
            };
            var plan = new BackupPlan { Name = "To the NAS", Target = target, KeepLast = 2, KeepWeekly = 0, KeepMonthly = 0, NextRunDate = DateTime.UtcNow.AddDays(1) };
            db.BackupPlans.Add(plan);
            await db.SaveChangesAsync();
            planId = plan.Id;
        }

        var service = _host.Services.GetRequiredService<BackupService>();
        BackupRun last = null!;
        var history = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            last = (await service.RunAsync(planId, BackupRunKind.Manual, CancellationToken.None))!;
            Assert.True(last.Status == BackupRunStatus.Succeeded, last.Message);
            history.Add($"{last.FileName} (pruned {last.Pruned})");
        }

        // two are left, the newest of the four among them
        var storage = new SmbBackupStorage(options);
        IReadOnlyList<RemoteBackupFile> files = await storage.ListAsync(CancellationToken.None);
        string story = "runs: " + string.Join("; ", history) + " | share: " + string.Join(", ", files.Select(f => f.Name));
        Assert.True(files.Count == 2, story);
        Assert.True(files.Any(f => f.Name == last.FileName), story);
        Assert.True(last.Pruned == 1, story);
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "tmp"), "*", SearchOption.AllDirectories));   // no scratch file is left behind

        // and what is on the share is a backup that can be restored: fetched and verified
        string local = Path.Combine(_root, "fetched.zip");
        await storage.DownloadAsync(last.FileName!, local, null, CancellationToken.None);
        using BackupArchive archive = BackupArchive.Open(local);
        await archive.VerifyAsync();
        Assert.Equal(last.Tables, archive.Describe().Tables);
    }

    [SmbDbFact]
    public async Task A_backup_on_a_share_is_fetched_before_a_restore_is_prepared()
    {
        SmbTargetOptions options = TestSmb.Options();
        long targetId;
        long planId;
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var plan = new BackupPlan
            {
                Name = "To the NAS",
                NextRunDate = DateTime.UtcNow.AddDays(1),
                Target = new BackupTarget
                {
                    Name = "NAS",
                    Kind = BackupTargetKind.Smb,
                    Host = options.Host,
                    Share = options.Share,
                    Path = options.Folder,
                    Domain = options.Domain,
                    Username = options.Username,
                    PasswordProtected = scope.ServiceProvider.GetRequiredService<MatMail.Services.SecretProtector>().Protect(options.Password!),
                },
            };
            db.BackupPlans.Add(plan);
            await db.SaveChangesAsync();
            planId = plan.Id;
            targetId = plan.TargetId;
        }

        BackupRun run = (await _host.Services.GetRequiredService<BackupService>().RunAsync(planId, BackupRunKind.Manual, CancellationToken.None))!;
        Assert.True(run.Status == BackupRunStatus.Succeeded, run.Message);

        var preparation = _host.Services.GetRequiredService<RestorePreparation>();
        Assert.True(preparation.Start(new RestoreRequest(targetId, run.FileName, null, null, true, true, true, "alice")));
        PreparationStatus status;
        DateTime giveUp = DateTime.UtcNow.AddSeconds(60);
        while ((status = preparation.Status).State == PreparationState.Running && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(50);
        }

        Assert.Equal(PreparationState.Restarting, status.State);
        PendingRestore request = PendingRestore.Read(DataDir)!;
        Assert.StartsWith(PendingRestore.IncomingFolder(DataDir), request.BackupPath);   // fetched into the volume
        Assert.True(request.DeleteBackupAfterwards);
        Assert.Equal(run.Bytes, new FileInfo(request.BackupPath).Length);
        using BackupArchive archive = BackupArchive.Open(request.BackupPath);
        await archive.VerifyAsync();
    }

    [SmbDbFact]
    public async Task A_share_that_cannot_be_reached_fails_the_run_with_the_reason()
    {
        SmbTargetOptions options = TestSmb.Options(password: "not-the-password");
        long planId;
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var plan = new BackupPlan
            {
                Name = "To the NAS",
                NextRunDate = DateTime.UtcNow.AddDays(1),
                Target = new BackupTarget
                {
                    Name = "NAS",
                    Kind = BackupTargetKind.Smb,
                    Host = options.Host,
                    Share = options.Share,
                    Path = options.Folder,
                    Username = options.Username,
                    PasswordProtected = scope.ServiceProvider.GetRequiredService<MatMail.Services.SecretProtector>().Protect("not-the-password"),
                },
            };
            db.BackupPlans.Add(plan);
            await db.SaveChangesAsync();
            planId = plan.Id;
        }

        BackupRun run = (await _host.Services.GetRequiredService<BackupService>().RunAsync(planId, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("password", run.Message);
    }
}

using System.Security.Cryptography;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using MatMail.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests;

public class LocalTargetPolicyTests
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matmail-policy-data");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "matmail-policy-outside");

    [Fact]
    public void A_folder_outside_the_data_volume_is_fine()
    {
        Assert.Null(LocalTargetPolicy.Validate(Path.Combine(_outside, "nas"), _data));
        Assert.Null(LocalTargetPolicy.Validate(_outside, _data));
    }

    [Fact]
    public void Inside_the_data_volume_only_the_backup_folder_may_be_used()
    {
        Assert.Null(LocalTargetPolicy.Validate(Path.Combine(_data, "backups"), _data));
        Assert.Null(LocalTargetPolicy.Validate(Path.Combine(_data, "backups", "weekly"), _data));

        // anywhere else the next backup would contain the backups before it
        Assert.NotNull(LocalTargetPolicy.Validate(_data, _data));
        Assert.NotNull(LocalTargetPolicy.Validate(Path.Combine(_data, "nas"), _data));
        Assert.NotNull(LocalTargetPolicy.Validate(Path.Combine(_data, "config"), _data));
        Assert.NotNull(LocalTargetPolicy.Validate(Path.Combine(_data, "backups", "..", "config"), _data));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/folder")]
    public void A_folder_must_be_given_as_a_full_path(string? path) => Assert.NotNull(LocalTargetPolicy.Validate(path, "/data"));

    [Fact]
    public void A_folder_with_dots_is_refused()
        => Assert.NotNull(LocalTargetPolicy.Validate(Path.Combine(Path.GetTempPath(), "a", "..", "b"), _data));

    [Fact]
    public void A_folder_that_only_starts_like_the_data_volume_is_outside_of_it()
        => Assert.Null(LocalTargetPolicy.Validate(_data + "-other", _data));

    [Theory]
    [InlineData("matmail-3f9a1c-p1-20261008-031500-1.0.zip", true)]
    [InlineData("a.zip", true)]
    [InlineData("../a.zip", false)]
    [InlineData("sub/a.zip", false)]
    [InlineData("sub\\a.zip", false)]
    [InlineData("c:a.zip", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    public void Only_plain_names_address_a_file_of_a_target(string name, bool plain)
        => Assert.Equal(plain, BackupStorageNames.IsPlainName(name));
}

public class BackupCoordinatorTests
{
    [Fact]
    public void Only_one_thing_runs_at_a_time()
    {
        var coordinator = new BackupCoordinator();

        using BackupLease? first = coordinator.TryBegin("backup", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(coordinator.TryBegin("another", CancellationToken.None));
        Assert.Equal("backup", coordinator.Current!.Title);

        first.Dispose();
        Assert.Null(coordinator.Current);
        using BackupLease? second = coordinator.TryBegin("another", CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public void The_progress_is_there_for_the_page_to_ask_for()
    {
        var coordinator = new BackupCoordinator();
        using BackupLease lease = coordinator.TryBegin("backup", CancellationToken.None)!;

        lease.Progress.Report(new BackupProgress("database", "MailMessage", 3, 30, 4096));
        // Progress<T> posts to the thread pool: wait for it
        SpinWait.SpinUntil(() => coordinator.Current!.Stage == "database", TimeSpan.FromSeconds(5));

        BackupActivity current = coordinator.Current!;
        Assert.Equal("MailMessage", current.Item);
        Assert.Equal(3, current.Done);
        Assert.Equal(30, current.Total);
        Assert.Equal(4096, current.Bytes);
    }

    [Fact]
    public void A_run_can_be_asked_to_stop()
    {
        var coordinator = new BackupCoordinator();
        Assert.False(coordinator.Cancel());

        using BackupLease lease = coordinator.TryBegin("backup", CancellationToken.None)!;
        Assert.False(lease.Token.IsCancellationRequested);
        Assert.True(coordinator.Cancel());
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public void Stopping_the_program_stops_the_run()
    {
        var coordinator = new BackupCoordinator();
        using var stopping = new CancellationTokenSource();
        using BackupLease lease = coordinator.TryBegin("backup", stopping.Token)!;

        stopping.Cancel();

        Assert.True(lease.Token.IsCancellationRequested);
    }
}

/// <summary>Plans and their runs against a real database and real folders.</summary>
public class BackupServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matmail-service-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;
    private Seed _seed = null!;

    private string DataDir => Path.Combine(_root, "data");
    private string Targets => Path.Combine(_root, "targets");
    private BackupService Service => _host.Services.GetRequiredService<BackupService>();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "config"));
        File.WriteAllText(Path.Combine(DataDir, "config", "app.json"), "{}");
        _host = await TestHost.CreateAsync(config => config.DataDir = DataDir);
        _seed = await _host.SeedAsync();
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

    [DbFact]
    public async Task A_run_makes_a_backup_in_the_target_and_keeps_its_history()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        string file = Assert.Single(Directory.GetFiles(Path.Combine(Targets, "Disk")));
        Assert.Equal(run.FileName, Path.GetFileName(file));
        BackupFileName name = BackupFiles.Parse(run.FileName!)!;
        Assert.Equal("p" + plan.Id, name.Label);
        Assert.Equal(BackupFiles.Short(await InstallationIdAsync()), name.Installation);

        using (BackupArchive archive = BackupArchive.Open(file))
        {
            await archive.VerifyAsync();
            Assert.Equal(archive.Describe().Tables, run.Tables);
            Assert.Equal(archive.Describe().Rows, run.Rows);
            Assert.Equal(archive.Describe().Files, run.Files);
            Assert.Contains(archive.Manifest.Files, f => f.Path == "config/app.json");
        }

        Assert.Equal(new FileInfo(file).Length, run.Bytes);
        Assert.False(run.Encrypted);
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "tmp"), "backup-*"));   // the scratch file is gone

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        BackupRun stored = await db.BackupRuns.AsNoTracking().SingleAsync();
        Assert.Equal(plan.Name, stored.PlanName);
        Assert.Equal("Disk", stored.TargetName);
        Assert.Equal(BackupRunKind.Manual, stored.Kind);
        Assert.NotNull(stored.FinishedDate);
        Assert.Equal(AppInfo.Version, stored.AppVersion);

        BackupPlan reloaded = await db.BackupPlans.AsNoTracking().SingleAsync();
        Assert.Equal(BackupRunStatus.Succeeded, reloaded.LastStatus);
        Assert.NotNull(reloaded.LastRunDate);
        Assert.Equal(0, reloaded.ConsecutiveFailures);
        Assert.Contains(await db.ActivityLogs.AsNoTracking().Where(a => a.Category == ActivityCategory.Backup).Select(a => a.Message).ToListAsync(), m => m.Contains(plan.Name));
    }

    [DbFact]
    public async Task A_scheduled_run_sets_the_next_time_and_a_manual_one_leaves_it()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));
        DateTime next = DateTime.UtcNow.AddDays(3);
        await SetNextRunAsync(plan.Id, next);

        await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None);
        Assert.Equal(next, (await ReloadAsync(plan.Id)).NextRunDate!.Value, TimeSpan.FromSeconds(1));

        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        DateTime after = (await ReloadAsync(plan.Id)).NextRunDate!.Value;
        Assert.True(after > DateTime.UtcNow);
        Assert.Equal(BackupSchedule.NextRunUtc(plan, DateTime.UtcNow, Service.Zone), after, TimeSpan.FromMinutes(1));
    }

    [DbFact]
    public async Task An_encrypted_plan_makes_an_encrypted_backup()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"), encryptWith: "correct horse battery staple");

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.True(run.Encrypted);
        string file = Path.Combine(Targets, "Disk", run.FileName!);
        Assert.EndsWith(".mmbak", file);
        Assert.True(BackupFiles.IsEncrypted(file));
        Assert.Throws<BackupPassphraseException>(() => BackupArchive.Open(file, "wrong"));
        using BackupArchive archive = BackupArchive.Open(file, "correct horse battery staple");
        await archive.VerifyAsync();
    }

    [DbFact]
    public async Task A_plan_that_should_encrypt_but_has_no_passphrase_fails_without_writing_anything()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));
        await using (var connection = new Npgsql.NpgsqlConnection(_host.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand($"UPDATE \"BackupPlan\" SET \"Encrypt\" = true, \"PassphraseProtected\" = NULL WHERE \"Id\" = {plan.Id}", connection);
            await command.ExecuteNonQueryAsync();
        }

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("passphrase", run.Message);
        Assert.Empty(Directory.Exists(Path.Combine(Targets, "Disk")) ? Directory.GetFiles(Path.Combine(Targets, "Disk")) : Array.Empty<string>());
    }

    [DbFact]
    public async Task Old_backups_are_removed_but_only_those_of_the_plan()
    {
        BackupTarget target = await AddLocalTargetAsync("Disk");
        BackupPlan plan = await AddPlanAsync(target, keepLast: 3);
        BackupPlan other = await AddPlanAsync(target, keepLast: 1, name: "Other plan");
        string installation = await InstallationIdAsync();
        string folder = Path.Combine(Targets, "Disk");
        Directory.CreateDirectory(folder);

        string Old(string label, int daysAgo, string? inst = null)
        {
            string name = BackupFiles.Name(DateTime.UtcNow.AddDays(-daysAgo), "0.9.0", false, label, inst ?? installation);
            File.WriteAllText(Path.Combine(folder, name), "old backup");
            return name;
        }

        string[] mine = [Old("p" + plan.Id, 5), Old("p" + plan.Id, 4), Old("p" + plan.Id, 3), Old("p" + plan.Id, 2), Old("p" + plan.Id, 1)];
        string[] ofOtherPlan = [Old("p" + other.Id, 9), Old("p" + other.Id, 8)];
        string[] ofOtherInstallation = [Old("p" + plan.Id, 9, "abcdef"), Old("p" + plan.Id, 8, "abcdef")];
        string[] manual = [Old(BackupFiles.ManualLabel, 20)];
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a backup");
        File.WriteAllText(Path.Combine(folder, "matmail-" + BackupFiles.Short(installation) + "-p" + plan.Id + "-20200101-000000-0.1.zip.partial"), "half");

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        string[] left = Directory.GetFiles(folder).Select(Path.GetFileName).Select(n => n!).ToArray();

        // the new one and the two newest of the old ones of this plan
        Assert.Contains(run.FileName, left);
        Assert.Equal(new[] { mine[3], mine[4] }, mine.Where(n => left.Contains(n)).ToArray());
        Assert.Equal(3, run.Pruned);

        // everything that is not this plan's stays: another plan's, another installation's, manual ones, and what is no backup
        Assert.All(ofOtherPlan.Concat(ofOtherInstallation).Concat(manual), name => Assert.Contains(name, left));
        Assert.Contains("notes.txt", left);
        Assert.Contains(left, n => n.EndsWith(".partial"));
    }

    [DbFact]
    public async Task A_failed_run_is_recorded_tried_again_soon_and_the_plan_goes_back_to_its_time_after_a_few()
    {
        string blocked = Path.Combine(Targets, "blocked");
        Directory.CreateDirectory(Targets);
        File.WriteAllText(blocked, "a file where the folder should be");
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Blocked", blocked));

        for (int attempt = 1; attempt <= BackupService.MaxRetries; attempt++)
        {
            BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None))!;
            Assert.Equal(BackupRunStatus.Failed, run.Status);
            Assert.False(string.IsNullOrWhiteSpace(run.Message));
            Assert.Null(run.FileName);

            BackupPlan state = await ReloadAsync(plan.Id);
            Assert.Equal(attempt, state.ConsecutiveFailures);
            Assert.Equal(BackupRunStatus.Failed, state.LastStatus);
            Assert.Equal(DateTime.UtcNow.AddMinutes(30), state.NextRunDate!.Value, TimeSpan.FromMinutes(1));
        }

        // after the last try the plan waits for its next regular time
        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        BackupPlan waiting = await ReloadAsync(plan.Id);
        Assert.Equal(BackupService.MaxRetries + 1, waiting.ConsecutiveFailures);
        Assert.Equal(BackupSchedule.NextRunUtc(plan, DateTime.UtcNow, Service.Zone), waiting.NextRunDate!.Value, TimeSpan.FromMinutes(1));

        // and the first success resets the count
        File.Delete(blocked);
        BackupRun fixedRun = (await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None))!;
        Assert.Equal(BackupRunStatus.Succeeded, fixedRun.Status);
        Assert.Equal(0, (await ReloadAsync(plan.Id)).ConsecutiveFailures);
    }

    [DbFact]
    public async Task A_target_in_the_data_volume_outside_the_backup_folder_fails_the_run_with_the_reason()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Wrong", Path.Combine(DataDir, "nas")));

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None))!;

        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("data volume", run.Message);
    }

    [DbFact]
    public async Task The_administrators_are_told_when_a_scheduled_run_fails_but_not_at_every_try()
    {
        await MakeAdministratorAsync(_seed.Alice.Id);
        string blocked = Path.Combine(Targets, "blocked");
        Directory.CreateDirectory(Targets);
        File.WriteAllText(blocked, "a file where the folder should be");
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Blocked", blocked));

        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        string[] first = await MessagesOfAsync(_seed.AliceMailbox.Id);
        Assert.Single(first);
        Assert.Contains("failed", first[0]);
        Assert.Contains(plan.Name, first[0]);
        Assert.Empty(await MessagesOfAsync(_seed.BobMailbox.Id));   // only administrators

        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        Assert.Single(await MessagesOfAsync(_seed.AliceMailbox.Id));

        // when it is given up there is one more
        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);
        Assert.Equal(2, (await MessagesOfAsync(_seed.AliceMailbox.Id)).Length);

        // a failure of a run started by hand is on the screen of the one who started it: no message
        await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None);
        Assert.Equal(2, (await MessagesOfAsync(_seed.AliceMailbox.Id)).Length);
    }

    [DbFact]
    public async Task A_plan_can_do_without_the_message()
    {
        await MakeAdministratorAsync(_seed.Alice.Id);
        string blocked = Path.Combine(Targets, "blocked");
        Directory.CreateDirectory(Targets);
        File.WriteAllText(blocked, "x");
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Blocked", blocked), notify: false);

        await Service.RunAsync(plan.Id, BackupRunKind.Scheduled, CancellationToken.None);

        Assert.Empty(await MessagesOfAsync(_seed.AliceMailbox.Id));
    }

    [DbFact]
    public async Task Only_one_run_at_a_time_and_a_second_asks_to_wait()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));
        var coordinator = _host.Services.GetRequiredService<BackupCoordinator>();

        using (coordinator.TryBegin("a restore is being prepared", CancellationToken.None))
        {
            Assert.Null(await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None));
            Assert.False(Service.StartInBackground(plan.Id, BackupRunKind.Manual));
        }

        Assert.NotNull(await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None));
    }

    [DbFact]
    public async Task A_run_started_in_the_background_ends_up_in_the_history()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));

        Assert.True(Service.StartInBackground(plan.Id, BackupRunKind.Manual));
        SpinWait.SpinUntil(() => Service.Coordinator.Current is null, TimeSpan.FromSeconds(60));

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        BackupRun run = await db.BackupRuns.AsNoTracking().SingleAsync();
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Single(Directory.GetFiles(Path.Combine(Targets, "Disk")));
    }

    [DbFact]
    public async Task A_cancelled_run_leaves_no_file_and_is_recorded()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        BackupRun run = (await Service.RunAsync(plan.Id, BackupRunKind.Manual, cancel.Token))!;

        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Equal("Cancelled.", run.Message);
        Assert.Empty(Directory.Exists(Path.Combine(Targets, "Disk")) ? Directory.GetFiles(Path.Combine(Targets, "Disk")) : Array.Empty<string>());
    }

    [DbFact]
    public async Task The_history_outlives_the_plan_and_a_target_in_use_cannot_be_deleted()
    {
        BackupTarget target = await AddLocalTargetAsync("Disk");
        BackupPlan plan = await AddPlanAsync(target);
        await Service.RunAsync(plan.Id, BackupRunKind.Manual, CancellationToken.None);

        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.BackupTargets.Remove(await db.BackupTargets.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.BackupPlans.Remove(await db.BackupPlans.SingleAsync());
            await db.SaveChangesAsync();
            BackupRun run = await db.BackupRuns.AsNoTracking().SingleAsync();
            Assert.Null(run.PlanId);
            Assert.Equal(plan.Name, run.PlanName);
        }
    }

    [DbFact]
    public async Task A_target_says_whether_it_can_be_used()
    {
        StorageCheck good = await Service.CheckAsync(new BackupTarget { Kind = BackupTargetKind.Local, Path = Path.Combine(Targets, "new-folder") }, null, CancellationToken.None);
        Assert.True(good.Ok, good.Message);
        Assert.NotNull(good.FreeBytes);
        Assert.True(Directory.Exists(Path.Combine(Targets, "new-folder")));
        Assert.Empty(Directory.GetFiles(Path.Combine(Targets, "new-folder")));   // the test file is gone

        StorageCheck inData = await Service.CheckAsync(new BackupTarget { Kind = BackupTargetKind.Local, Path = Path.Combine(DataDir, "config") }, null, CancellationToken.None);
        Assert.False(inData.Ok);

        StorageCheck relative = await Service.CheckAsync(new BackupTarget { Kind = BackupTargetKind.Local, Path = "somewhere" }, null, CancellationToken.None);
        Assert.False(relative.Ok);
    }

    // ---------------------------------------------------------------------------------------------
    // The scheduler
    // ---------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_scheduler_runs_what_is_due_and_nothing_else()
    {
        BackupTarget target = await AddLocalTargetAsync("Disk");
        BackupTarget off = await AddLocalTargetAsync("Switched off", active: false);
        BackupPlan due = await AddPlanAsync(target, name: "Due");
        BackupPlan later = await AddPlanAsync(target, name: "Later");
        BackupPlan inactive = await AddPlanAsync(target, name: "Inactive", active: false);
        BackupPlan offTarget = await AddPlanAsync(off, name: "On a target that is off");
        BackupPlan fresh = await AddPlanAsync(target, name: "Fresh");
        await SetNextRunAsync(due.Id, DateTime.UtcNow.AddMinutes(-5));
        await SetNextRunAsync(later.Id, DateTime.UtcNow.AddHours(5));
        await SetNextRunAsync(inactive.Id, DateTime.UtcNow.AddMinutes(-5));
        await SetNextRunAsync(offTarget.Id, DateTime.UtcNow.AddMinutes(-5));
        await SetNextRunAsync(fresh.Id, null);

        var scheduler = new BackupScheduler(_host.Services.GetRequiredService<IServiceScopeFactory>(), Service, _host.Config, NullLogger<BackupScheduler>.Instance);
        await scheduler.RunDueAsync(CancellationToken.None);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        string[] ran = await db.BackupRuns.AsNoTracking().Select(r => r.PlanName).ToArrayAsync();
        Assert.Equal(new[] { "Due" }, ran);
        Assert.Single(Directory.GetFiles(Path.Combine(Targets, "Disk")));

        Assert.True((await ReloadAsync(due.Id)).NextRunDate > DateTime.UtcNow);                                  // the next regular time
        Assert.Equal(BackupRunStatus.Succeeded, (await ReloadAsync(due.Id)).LastStatus);
        Assert.Null((await ReloadAsync(later.Id)).LastRunDate);
        Assert.Null((await ReloadAsync(inactive.Id)).LastRunDate);
        Assert.Null((await ReloadAsync(offTarget.Id)).LastRunDate);

        // a plan without a time gets one, and does not run at once
        BackupPlan scheduled = await ReloadAsync(fresh.Id);
        Assert.Null(scheduled.LastRunDate);
        Assert.True(scheduled.NextRunDate > DateTime.UtcNow);
    }

    [DbFact]
    public async Task The_scheduler_runs_several_due_plans_one_after_the_other()
    {
        BackupTarget target = await AddLocalTargetAsync("Disk");
        BackupPlan a = await AddPlanAsync(target, name: "A");
        BackupPlan b = await AddPlanAsync(target, name: "B");
        await SetNextRunAsync(a.Id, DateTime.UtcNow.AddMinutes(-10));
        await SetNextRunAsync(b.Id, DateTime.UtcNow.AddMinutes(-20));

        var scheduler = new BackupScheduler(_host.Services.GetRequiredService<IServiceScopeFactory>(), Service, _host.Config, NullLogger<BackupScheduler>.Instance);
        await scheduler.RunDueAsync(CancellationToken.None);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal(new[] { "B", "A" }, await db.BackupRuns.AsNoTracking().OrderBy(r => r.Id).Select(r => r.PlanName).ToArrayAsync());   // the one that waited longest first
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Targets, "Disk")).Length);
    }

    [DbFact]
    public async Task The_scheduler_leaves_a_plan_due_while_something_else_runs()
    {
        BackupPlan plan = await AddPlanAsync(await AddLocalTargetAsync("Disk"));
        await SetNextRunAsync(plan.Id, DateTime.UtcNow.AddMinutes(-5));
        var scheduler = new BackupScheduler(_host.Services.GetRequiredService<IServiceScopeFactory>(), Service, _host.Config, NullLogger<BackupScheduler>.Instance);

        using (_host.Services.GetRequiredService<BackupCoordinator>().TryBegin("manual", CancellationToken.None))
        {
            await scheduler.RunDueAsync(CancellationToken.None);
        }

        Assert.Null((await ReloadAsync(plan.Id)).LastRunDate);
        Assert.True((await ReloadAsync(plan.Id)).NextRunDate < DateTime.UtcNow);   // still due: the next look runs it
        await scheduler.RunDueAsync(CancellationToken.None);
        Assert.NotNull((await ReloadAsync(plan.Id)).LastRunDate);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<BackupTarget> AddLocalTargetAsync(string name, string? path = null, bool active = true)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var target = new BackupTarget { Name = name, Kind = BackupTargetKind.Local, Path = path ?? Path.Combine(Targets, name), IsActive = active };
        db.BackupTargets.Add(target);
        await db.SaveChangesAsync();
        return target;
    }

    private async Task<BackupPlan> AddPlanAsync(BackupTarget target, string name = "Daily", int keepLast = 7, string? encryptWith = null, bool notify = true, bool active = true)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var plan = new BackupPlan
        {
            Name = name,
            TargetId = target.Id,
            KeepLast = keepLast,
            KeepWeekly = 0,
            KeepMonthly = 0,
            Encrypt = encryptWith is not null,
            PassphraseProtected = encryptWith is null ? null : scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect(encryptWith),
            NotifyOnFailure = notify,
            IsActive = active,
            NextRunDate = DateTime.UtcNow.AddDays(1),
        };
        db.BackupPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    private async Task<string> InstallationIdAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await SystemSettings.GetOrCreateInstallationIdAsync(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    private async Task<BackupPlan> ReloadAsync(long id)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().BackupPlans.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task SetNextRunAsync(long id, DateTime? next)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        await db.BackupPlans.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.NextRunDate, next));
    }

    private async Task MakeAdministratorAsync(long userId)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSystemAdmin, true));
    }

    private async Task<string[]> MessagesOfAsync(long mailboxId)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        return await db.MailMessages.IgnoreQueryFilters().AsNoTracking().Where(m => m.MailboxId == mailboxId).OrderBy(m => m.Id).Select(m => m.Subject).ToArrayAsync();
    }
}

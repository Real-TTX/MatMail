using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Versioning;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Backup;

/// <summary>What is going on right now (shown on the backup page, polled while it runs).</summary>
public sealed record BackupActivity(string Title, string Stage, string? Item, int Done, int Total, long Bytes, DateTime StartedUtc);

/// <summary>Only one backup (or the preparation of a restore) runs at a time; this is the place that is taken, and where the progress is kept.</summary>
public sealed class BackupCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private CancellationTokenSource? _cancel;
    private BackupActivity? _current;

    public BackupActivity? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Takes the place; null when something else is running.</summary>
    public BackupLease? TryBegin(string title, CancellationToken outer)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_lock)
        {
            _cancel = source;
            _current = new BackupActivity(title, "start", null, 0, 0, 0, DateTime.UtcNow);
        }

        return new BackupLease(this, source);
    }

    /// <summary>Asks the running one to stop. False when nothing runs.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_cancel is null)
            {
                return false;
            }

            _cancel.Cancel();
            return true;
        }
    }

    internal void Update(BackupProgress progress)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                _current = _current with { Stage = progress.Stage, Item = progress.Item, Done = progress.Done, Total = progress.Total, Bytes = progress.Bytes };
            }
        }
    }

    internal void End(CancellationTokenSource source)
    {
        lock (_lock)
        {
            _current = null;
            _cancel = null;
        }

        source.Dispose();
        _gate.Release();
    }
}

public sealed class BackupLease(BackupCoordinator coordinator, CancellationTokenSource source) : IDisposable
{
    private int _disposed;

    public CancellationToken Token => source.Token;
    public IProgress<BackupProgress> Progress { get; } = new Progress<BackupProgress>(coordinator.Update);

    /// <summary>Gives the place back (once: a second call does nothing).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            coordinator.End(source);
        }
    }
}

/// <summary>
/// Makes the backups of the plans: writes the file (in scratch space, checked, encrypted when the plan says so), puts it into the target,
/// removes what the retention rules no longer want there, keeps the history, and tells the administrators when a scheduled run fails.
/// </summary>
public sealed class BackupService
{
    /// <summary>A scheduled backup that failed is tried again this often, 30 minutes apart, before the plan waits for its next regular time.</summary>
    public const int MaxRetries = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly BackupStorageFactory _storages;
    private readonly SecretProtector _secrets;
    private readonly BackupCoordinator _coordinator;
    private readonly ActivityLogger _activity;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IServiceScopeFactory scopes,
        AppConfig config,
        BackupStorageFactory storages,
        SecretProtector secrets,
        BackupCoordinator coordinator,
        ActivityLogger activity,
        ILogger<BackupService> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        _scopes = scopes;
        _config = config;
        _storages = storages;
        _secrets = secrets;
        _coordinator = coordinator;
        _activity = activity;
        _lifetime = lifetime;
        _logger = logger;
        Zone = FindZone(config.Display.TimeZone);
    }

    /// <summary>The time zone the schedules are in (the one of the installation).</summary>
    public TimeZoneInfo Zone { get; }

    public BackupCoordinator Coordinator => _coordinator;

    private static TimeZoneInfo FindZone(string? id)
    {
        try
        {
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Starts a run in the background (for the button on the backup page). False when something is running already.</summary>
    public bool StartInBackground(long planId, BackupRunKind kind)
    {
        BackupLease? lease = _coordinator.TryBegin("backup", _lifetime?.ApplicationStopping ?? CancellationToken.None);
        if (lease is null)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            using (lease)
            {
                await RunAsync(planId, kind, lease);
            }
        });
        return true;
    }

    /// <summary>Runs a plan now and waits for it (the scheduler). Null when something else is running.</summary>
    public async Task<BackupRun?> RunAsync(long planId, BackupRunKind kind, CancellationToken cancel)
    {
        using BackupLease? lease = _coordinator.TryBegin("backup", cancel);
        return lease is null ? null : await RunAsync(planId, kind, lease);
    }

    private async Task<BackupRun> RunAsync(long planId, BackupRunKind kind, BackupLease lease)
    {
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        BackupPlan plan = await db.BackupPlans.Include(p => p.Target).FirstAsync(p => p.Id == planId);

        var run = new BackupRun
        {
            PlanId = plan.Id,
            PlanName = plan.Name,
            TargetId = plan.TargetId,
            TargetName = plan.Target!.Name,
            Kind = kind,
            StartedDate = DateTime.UtcNow,
            AppVersion = AppInfo.Version,
        };

        try
        {
            await MakeBackupAsync(db, plan, run, lease);
            run.Status = BackupRunStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            run.Status = BackupRunStatus.Failed;
            run.Message = "Cancelled.";
        }
        catch (Exception ex)
        {
            run.Status = BackupRunStatus.Failed;
            run.Message = ex is BackupStorageException or BackupCorruptException or IOException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            _logger.LogError(ex, "The backup {Plan} failed.", plan.Name);
        }

        run.FinishedDate = DateTime.UtcNow;
        await RecordAsync(db, plan, run, kind);
        return run;
    }

    private async Task MakeBackupAsync(MatMailDbContext db, BackupPlan plan, BackupRun run, BackupLease lease)
    {
        string? passphrase = null;
        if (plan.Encrypt)
        {
            passphrase = _secrets.Unprotect(plan.PassphraseProtected);
            if (string.IsNullOrEmpty(passphrase))
            {
                throw new BackupStorageException("The plan is set to encrypt, but its passphrase is missing or cannot be read: enter it again.");
            }
        }

        using IBackupStorage storage = _storages.Create(plan.Target!);
        string installation = await SystemSettings.GetOrCreateInstallationIdAsync(db);
        string name = await UniqueNameAsync(storage, passphrase is not null, "p" + plan.Id, installation, lease.Token);
        string scratch = _storages.ScratchDirectory;
        var source = new BackupSource(_config.Database.ConnectionString, _config.DataDir) { ExcludedDirectories = storage.LocalFolder is { } folder ? [folder] : [] };

        BackupFileResult result;
        if (storage.LocalFolder is { } local)
        {
            result = await BackupJob.CreateFileAsync(source, Path.Combine(local, name), passphrase, scratch, lease.Progress, plan.Verify, lease.Token);
        }
        else
        {
            result = await BackupJob.CreateFileAsync(source, Path.Combine(scratch, "outgoing", name), passphrase, scratch, lease.Progress, plan.Verify, lease.Token);
            try
            {
                var uploaded = new Progress<long>(bytes => lease.Progress.Report(new BackupProgress("upload", name, 0, 0, bytes)));
                await storage.UploadAsync(result.Path, name, uploaded, lease.Token);
            }
            finally
            {
                TryDelete(result.Path);
            }
        }

        run.FileName = name;
        run.Bytes = result.Bytes;
        run.Encrypted = result.Encrypted;
        run.Tables = result.Manifest.Database.Tables.Count;
        run.Rows = result.Manifest.Database.Tables.Sum(t => t.Rows);
        run.Files = result.Manifest.Files.Count;

        try
        {
            run.Pruned = await PruneAsync(plan, storage, installation, lease.Token);
        }
        catch (Exception ex) when (ex is BackupStorageException or IOException)
        {
            // the backup is made; that old ones could not be removed is a note, not a failure
            _logger.LogWarning("Old backups of {Plan} could not be removed: {Message}", plan.Name, ex.Message);
            run.Message = "Old backups could not be removed: " + ex.Message;
        }
    }

    /// <summary>A name that is not taken in the target (names have a resolution of a second: a run right after another would meet its file).</summary>
    private static async Task<string> UniqueNameAsync(IBackupStorage storage, bool encrypted, string label, string installation, CancellationToken cancel)
    {
        HashSet<string> taken = (await storage.ListAsync(cancel)).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTime stamp = DateTime.UtcNow;
        string name;
        do
        {
            name = BackupFiles.Name(stamp, AppInfo.Version, encrypted, label, installation);
            stamp = stamp.AddSeconds(1);
        }
        while (taken.Contains(name));

        return name;
    }

    /// <summary>Removes the backups of this plan (and only of this plan and installation) that the retention rules no longer want.</summary>
    public async Task<int> PruneAsync(BackupPlan plan, IBackupStorage storage, string installationId, CancellationToken cancel)
    {
        string installation = BackupFiles.Short(installationId);
        string label = "p" + plan.Id;
        var mine = new List<(string Name, DateTime CreatedUtc)>();
        foreach (RemoteBackupFile file in await storage.ListAsync(cancel))
        {
            BackupFileName? parsed = BackupFiles.Parse(file.Name);
            if (parsed is not null && parsed.Installation == installation && parsed.Label == label)
            {
                mine.Add((file.Name, parsed.CreatedUtc));
            }
        }

        IReadOnlyList<string> expired = BackupRetention.Expired(mine, RetentionPolicy.Of(plan), DateTime.UtcNow, Zone);
        foreach (string name in expired)
        {
            await storage.DeleteAsync(name, cancel);
        }

        return expired.Count;
    }

    private async Task RecordAsync(MatMailDbContext db, BackupPlan plan, BackupRun run, BackupRunKind kind)
    {
        bool failed = run.Status == BackupRunStatus.Failed;
        DateTime now = DateTime.UtcNow;

        plan.LastRunDate = now;
        plan.LastStatus = run.Status;
        plan.ConsecutiveFailures = failed ? plan.ConsecutiveFailures + 1 : 0;
        if (kind == BackupRunKind.Scheduled)
        {
            // a failure is tried again soon, a few times; after that the plan waits for its next regular time
            plan.NextRunDate = failed && plan.ConsecutiveFailures <= MaxRetries
                ? now.AddMinutes(30)
                : BackupSchedule.NextRunUtc(plan, now, Zone);
        }

        db.BackupRuns.Add(run);
        await db.SaveChangesAsync();

        if (!failed)
        {
            await _activity.InfoAsync(ActivityCategory.Backup, $"Backup “{plan.Name}” made: {run.FileName} ({run.Bytes / (1024.0 * 1024.0):0.0} MB) in “{run.TargetName}”.", run.Pruned > 0 ? $"{run.Pruned} old backups removed." : null);
            return;
        }

        await _activity.ErrorAsync(ActivityCategory.Backup, $"Backup “{plan.Name}” failed.", run.Message);
        bool firstFailure = plan.ConsecutiveFailures == 1;
        bool givenUp = plan.ConsecutiveFailures == MaxRetries + 1;
        if (kind == BackupRunKind.Scheduled && plan.NotifyOnFailure && (firstFailure || givenUp))
        {
            await NotifyAsync(db, plan, run, givenUp);
        }
    }

    /// <summary>A message in the mailbox of every system administrator, delivered inside the gateway (it needs no mail provider to arrive).</summary>
    private async Task NotifyAsync(MatMailDbContext db, BackupPlan plan, BackupRun run, bool givenUp)
    {
        try
        {
            List<string> addresses = await db.MailboxAliases.IgnoreQueryFilters()
                .Where(a => a.IsPrimary && a.Mailbox!.IsActive && a.Mailbox.OwnerUser != null && a.Mailbox.OwnerUser.IsSystemAdmin && a.Mailbox.OwnerUser.IsActive)
                .Select(a => a.Address)
                .Distinct()
                .ToListAsync();
            if (addresses.Count == 0)
            {
                return;
            }

            string host = string.IsNullOrWhiteSpace(_config.Server.Hostname) ? "localhost" : _config.Server.Hostname;
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("MatMail", "postmaster@" + host));
            foreach (string address in addresses)
            {
                message.To.Add(MailboxAddress.Parse(address));
            }

            message.Subject = $"[MatMail] Backup “{plan.Name}” failed";
            message.Date = DateTimeOffset.UtcNow;
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(host);
            message.Body = new TextPart("plain")
            {
                Text = $"The scheduled backup “{plan.Name}” to “{run.TargetName}” failed.\r\n\r\n{run.Message}\r\n\r\n"
                       + (givenUp
                           ? $"It was tried {plan.ConsecutiveFailures} times; the next try is at the next regular time of the plan.\r\n"
                           : "It is tried again in 30 minutes.\r\n")
                       + "\r\nBackups: Administration → Backups.\r\n",
            };

            using var stream = new MemoryStream();
            message.WriteTo(stream);
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(stream.ToArray(), new DeliverySource { EnvelopeRecipients = addresses.ToArray() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The administrators could not be told about the failed backup.");
        }
    }

    /// <summary>Connects to a target (not saved yet, or saved) and says what it found.</summary>
    public async Task<StorageCheck> CheckAsync(BackupTarget target, string? plainPassword, CancellationToken cancel)
    {
        try
        {
            using IBackupStorage storage = _storages.Create(target, plainPassword);
            return await storage.CheckAsync(cancel);
        }
        catch (BackupStorageException ex)
        {
            return new StorageCheck(false, ex.Message, null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // scratch space: the next clean-up gets it
        }
    }
}

/// <summary>Starts the plans when they are due.</summary>
public sealed class BackupScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackupService _service;
    private readonly AppConfig _config;
    private readonly ILogger<BackupScheduler> _logger;
    private readonly BackupDownloads? _downloads;
    private readonly RestorePreparation? _preparation;

    public BackupScheduler(IServiceScopeFactory scopes, BackupService service, AppConfig config, ILogger<BackupScheduler> logger, BackupDownloads? downloads = null, RestorePreparation? preparation = null)
    {
        _scopes = scopes;
        _service = service;
        _config = config;
        _logger = logger;
        _downloads = downloads;
        _preparation = preparation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not right at the start: the database may still be coming up, and what is overdue is better done when everything else is running.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.Backup.Enabled)
                {
                    await RunDueAsync(stoppingToken);
                }

                _downloads?.CleanUp(TimeSpan.FromHours(1));
                _preparation?.CleanUp(TimeSpan.FromHours(24));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The backup scheduler failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs every plan whose time has come, one after the other (also what came due while the program was off).</summary>
    public async Task RunDueAsync(CancellationToken cancel)
    {
        List<long> due;
        using (IServiceScope scope = _scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            DateTime now = DateTime.UtcNow;

            // a plan without a next time (just switched on) gets one; it does not run at once
            List<BackupPlan> unscheduled = await db.BackupPlans.Where(p => p.IsActive && p.NextRunDate == null).ToListAsync(cancel);
            foreach (BackupPlan plan in unscheduled)
            {
                plan.NextRunDate = BackupSchedule.NextRunUtc(plan, now, _service.Zone);
            }

            if (unscheduled.Count > 0)
            {
                await db.SaveChangesAsync(cancel);
            }

            due = await db.BackupPlans
                .Where(p => p.IsActive && p.Target!.IsActive && p.NextRunDate != null && p.NextRunDate <= now)
                .OrderBy(p => p.NextRunDate)
                .Select(p => p.Id)
                .ToListAsync(cancel);
        }

        foreach (long id in due)
        {
            cancel.ThrowIfCancellationRequested();
            BackupRun? run = await _service.RunAsync(id, BackupRunKind.Scheduled, cancel);
            if (run is null)
            {
                return;   // something else (a backup started by hand) is running: the plan is still due at the next look
            }
        }
    }
}

public static class BackupRegistration
{
    public static IServiceCollection AddBackups(this IServiceCollection services)
    {
        services.AddSingleton<BackupStorageFactory>();
        services.AddSingleton<BackupCoordinator>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<BackupDownloads>();
        services.AddSingleton<RestorePreparation>();
        return services;
    }
}

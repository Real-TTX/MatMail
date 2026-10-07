using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailSync;

/// <summary>
/// The entry point for the admin pages: "Sync now" either as a prioritised request to the background scheduler
/// (<see cref="RequestSync"/>, returns at once) or inline with the result (<see cref="SyncNowAsync"/>).
/// </summary>
public sealed class MailSyncTrigger
{
    private readonly MailSyncRunner _runner;
    private readonly object _lock = new();
    private readonly List<long> _requests = new();
    private TaskCompletionSource _signal = NewSignal();

    public MailSyncTrigger(MailSyncRunner runner) => _runner = runner;

    /// <summary>Asks the scheduler to synchronise the account as soon as a slot is free, ahead of the due accounts.</summary>
    public void RequestSync(long accountId)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (!_requests.Contains(accountId))
            {
                _requests.Add(accountId);
            }

            signal = _signal;
            _signal = NewSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Synchronises the account right now and waits for the outcome (a run that is already going is not started twice).</summary>
    public Task<SyncReport> SyncNowAsync(long accountId, CancellationToken cancel = default) => _runner.RunAsync(accountId, cancel);

    /// <summary>
    /// Makes the next run compare everything again (see <see cref="MailSyncRunner.ResetAsync"/>); for when the account's server,
    /// user or role changed. Returns false while a run is going.
    /// </summary>
    public Task<bool> ResetAsync(long accountId, CancellationToken cancel = default) => _runner.ResetAsync(accountId, cancel);

    public bool IsRunning(long accountId) => _runner.IsRunning(accountId);

    public bool IsRequested(long accountId)
    {
        lock (_lock)
        {
            return _requests.Contains(accountId);
        }
    }

    /// <summary>Completes when the next request comes in.</summary>
    internal Task WhenRequested
    {
        get
        {
            lock (_lock)
            {
                return _signal.Task;
            }
        }
    }

    /// <summary>Takes up to <paramref name="max"/> requested accounts that are not busy; busy ones stay requested.</summary>
    internal List<long> TakeRequests(int max, IReadOnlySet<long> busy)
    {
        lock (_lock)
        {
            List<long> taken = _requests.Where(id => !busy.Contains(id)).Take(Math.Max(0, max)).ToList();
            _requests.RemoveAll(taken.Contains);
            return taken;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The background scheduler (on unless Sync.Enabled is false): every few seconds it starts the accounts that are due (enabled,
/// receiving, NextSyncDate reached, not running), at most Sync.MaxParallel at a time and requested ones first. It wakes up early
/// for "Sync now" requests and when a run ends.
/// </summary>
public sealed class MailSyncService : BackgroundService
{
    private readonly MailSyncRunner _runner;
    private readonly MailSyncTrigger _trigger;
    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly MailSyncOptions _options;
    private readonly ILogger<MailSyncService> _logger;
    private readonly Dictionary<long, Task> _running = new();

    public MailSyncService(
        MailSyncRunner runner, MailSyncTrigger trigger, IServiceScopeFactory scopes, AppConfig config, MailSyncOptions options, ILogger<MailSyncService> logger)
    {
        _runner = runner;
        _trigger = trigger;
        _scopes = scopes;
        _config = config;
        _options = options;
        _logger = logger;
    }

    private int MaxParallel => Math.Max(1, _config.Sync.MaxParallel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Sync.Enabled)
        {
            _logger.LogInformation("The provider synchronisation is switched off (Sync.Enabled = false).");
            return;
        }

        _logger.LogInformation("Provider synchronisation started (at most {MaxParallel} accounts at a time).", MaxParallel);
        while (!stoppingToken.IsCancellationRequested)
        {
            Task requested = _trigger.WhenRequested;
            try
            {
                await StartDueRunsAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Looking for due provider accounts failed; trying again shortly.");
            }

            await WaitForWorkAsync(requested, stoppingToken);
        }

        await WhenIdleAsync();
    }

    /// <summary>
    /// One scheduling pass: starts the requested and then the due accounts as far as slots are free. Returns the ids of the
    /// accounts started; their runs go on in the background (see <see cref="WhenIdleAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<long>> StartDueRunsAsync(DateTime now, CancellationToken cancel)
    {
        foreach (long finished in _running.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList())
        {
            _running.Remove(finished);
        }

        int free = MaxParallel - _running.Count;
        if (free <= 0)
        {
            return Array.Empty<long>();
        }

        var busy = new HashSet<long>(_running.Keys.Concat(_runner.RunningAccountIds));
        List<long> start = _trigger.TakeRequests(free, busy);
        if (start.Count < free)
        {
            busy.UnionWith(start);
            start.AddRange(await FindDueAccountsAsync(now, free - start.Count, busy, cancel));
        }

        foreach (long accountId in start)
        {
            _running[accountId] = RunSafelyAsync(accountId, cancel);
        }

        return start;
    }

    /// <summary>
    /// The accounts that are due at <paramref name="now"/>: enabled, receiving (not send-only, a protocol and a host set), of an
    /// active tenant, NextSyncDate reached (never synchronised first), and not running (or running so long without a sign of life
    /// that the run must have crashed).
    /// </summary>
    public async Task<IReadOnlyList<long>> FindDueAccountsAsync(DateTime now, int limit, IReadOnlyCollection<long> exclude, CancellationToken cancel = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<long>();
        }

        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        DateTime staleBefore = now - _options.StaleRunAfter;
        long[] excluded = exclude.ToArray();

        return await db.MailAccounts.AsNoTracking()
            .Where(a => a.IsEnabled && a.Role != MailAccountRole.SendOnly && a.ReceiveProtocol != ReceiveProtocol.None)
            .Where(a => a.ReceiveHost != null && a.ReceiveHost != string.Empty)
            .Where(a => a.NextSyncDate == null || a.NextSyncDate <= now)
            .Where(a => a.LastSyncState != SyncState.Running || a.UpdateDate < staleBefore)
            .Where(a => db.Tenants.Any(t => t.Id == a.TenantId && t.IsActive))
            .Where(a => !excluded.Contains(a.Id))
            .OrderBy(a => a.NextSyncDate == null ? 0 : 1)
            .ThenBy(a => a.NextSyncDate)
            .ThenBy(a => a.Id)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancel);
    }

    /// <summary>Waits until the runs started by the scheduler have ended (shutdown, tests).</summary>
    public async Task WhenIdleAsync()
    {
        try
        {
            await Task.WhenAll(_running.Values.ToArray());
        }
        catch (Exception)
        {
            // RunSafelyAsync does not throw; nothing to report here.
        }
    }

    private async Task RunSafelyAsync(long accountId, CancellationToken cancel)
    {
        // Leave the scheduler loop at once; the run goes on in the background.
        await Task.Yield();
        try
        {
            SyncReport report = await _runner.RunAsync(accountId, cancel);
            _logger.LogDebug("Account {AccountId} synchronised: {Message}", accountId, report.Message);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The synchronisation of account {AccountId} ended unexpectedly.", accountId);
        }
    }

    /// <summary>Sleeps until the next tick, a "Sync now" request or the end of a run (which frees a slot).</summary>
    private async Task WaitForWorkAsync(Task requested, CancellationToken stoppingToken)
    {
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var waits = new List<Task> { Task.Delay(_options.TickInterval, tick.Token), requested };
        waits.AddRange(_running.Values);
        await Task.WhenAny(waits);
        await tick.CancelAsync();
    }
}

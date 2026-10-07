using MatMail.Configuration;

namespace MatMail.MailSync;

/// <summary>
/// The entry point for the admin pages: "Sync now" either as a prioritised request to the background scheduler
/// (<see cref="RequestSync"/>, returns at once) or inline with the result (<see cref="SyncNowAsync"/>).
/// </summary>
public sealed class MailSyncTrigger
{
    private readonly MailSyncRunner _runner;
    private readonly AppConfig _config;
    private readonly object _lock = new();
    private readonly List<long> _requests = new();
    private TaskCompletionSource _signal = NewSignal();

    public MailSyncTrigger(MailSyncRunner runner, AppConfig config)
    {
        _runner = runner;
        _config = config;
    }

    /// <summary>False when the synchronisation is switched off in the settings (Sync.Enabled); then nothing is fetched at all.</summary>
    public bool IsEnabled => _config.Sync.Enabled;

    /// <summary>Asks the scheduler to synchronise the account as soon as a slot is free, ahead of the due accounts.</summary>
    public void RequestSync(long accountId)
    {
        if (!IsEnabled)
        {
            return;
        }

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

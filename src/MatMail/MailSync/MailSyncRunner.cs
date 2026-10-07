using System.Collections.Concurrent;
using System.Diagnostics;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailSync;

/// <summary>
/// Runs the synchronisation of one account, never twice at the same time: an in-process lock plus the persisted
/// <see cref="SyncState.Running"/> state (a crashed run leaves it behind; it counts as stale after
/// <see cref="MailSyncOptions.StaleRunAfter"/> without a sign of life). Afterwards the account gets its state, a readable message,
/// the failure count and the next due date (exponential back-off after failures), and the activity log gets one line when
/// something happened or failed (repeated identical errors are not logged again).
/// </summary>
public sealed class MailSyncRunner
{
    private const int MaxMessageLength = 2000;

    private readonly IServiceScopeFactory _scopes;
    private readonly ActivityLogger _activity;
    private readonly SyncFailureTracker _failures;
    private readonly AppConfig _config;
    private readonly MailSyncOptions _options;
    private readonly ILogger<MailSyncRunner> _logger;
    private readonly ConcurrentDictionary<long, byte> _running = new();

    public MailSyncRunner(
        IServiceScopeFactory scopes, ActivityLogger activity, SyncFailureTracker failures, AppConfig config, MailSyncOptions options, ILogger<MailSyncRunner> logger)
    {
        _scopes = scopes;
        _activity = activity;
        _failures = failures;
        _config = config;
        _options = options;
        _logger = logger;
    }

    public bool IsRunning(long accountId) => _running.ContainsKey(accountId);

    public IReadOnlyCollection<long> RunningAccountIds => _running.Keys.ToArray();

    /// <summary>Why an account is not synchronised at all (null: it is).</summary>
    public static string? RefusalOf(MailAccount account)
    {
        if (!account.IsEnabled)
        {
            return "The account is disabled.";
        }

        if (account.Role == MailAccountRole.SendOnly)
        {
            return "The account only sends mail; nothing is fetched.";
        }

        return account.ReceiveProtocol == ReceiveProtocol.None || string.IsNullOrWhiteSpace(account.ReceiveHost)
            ? "No receiving server is configured."
            : null;
    }

    /// <summary>Synchronises the account now and reports the outcome (also when it could not start).</summary>
    public async Task<SyncReport> RunAsync(long accountId, CancellationToken cancel = default)
    {
        if (!_config.Sync.Enabled)
        {
            return SyncReport.NotStarted(accountId, "The synchronisation is switched off in the settings (Sync.Enabled).");
        }

        if (!_running.TryAdd(accountId, 0))
        {
            return SyncReport.NotStarted(accountId, "A synchronisation of this account is already running.");
        }

        try
        {
            return await RunExclusiveAsync(accountId, cancel);
        }
        finally
        {
            _running.TryRemove(accountId, out _);
        }
    }

    /// <summary>
    /// Forgets what was fetched for the account (folder positions and message records), so the next run compares every remote
    /// message again. Local mail stays; only live-access stand-ins are removed and listed afresh. Meant for when the account's
    /// server, user or role changed (UIDs of another server mean other messages). Returns false while a run is going.
    /// </summary>
    public async Task<bool> ResetAsync(long accountId, CancellationToken cancel = default)
    {
        if (!_running.TryAdd(accountId, 0))
        {
            return false;
        }

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            var current = scope.ServiceProvider.GetRequiredService<CurrentUser>();
            current.RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            MailAccount? account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancel);
            if (account is null || (account.LastSyncState == SyncState.Running && account.UpdateDate >= DateTime.UtcNow - _options.StaleRunAfter))
            {
                return false;
            }

            current.RunAsSystemInTenant(account.TenantId);
            long[] stubs = await db.MailMessages
                .Where(m => m.SourceAccountId == accountId && m.Storage == MessageStorage.Remote)
                .Select(m => m.Id)
                .ToArrayAsync(cancel);
            if (stubs.Length > 0)
            {
                await scope.ServiceProvider.GetRequiredService<MailStore>().DeleteAsync(stubs, permanent: true, cancel);
            }

            await db.RemoteMessageStates.Where(r => r.MailAccountId == accountId).ExecuteDeleteAsync(cancel);
            await db.MailAccountFolderStates.Where(s => s.MailAccountId == accountId).ExecuteDeleteAsync(cancel);
            await db.MailAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.NextSyncDate, (DateTime?)null)
                .SetProperty(a => a.FailureCount, 0), cancel);
            _failures.ForgetAccount(accountId);
            _logger.LogInformation("The synchronisation state of account {AccountId} was reset.", accountId);
            return true;
        }
        finally
        {
            _running.TryRemove(accountId, out _);
        }
    }

    private async Task<SyncReport> RunExclusiveAsync(long accountId, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        (MailAccount? account, string? refusal) = await ClaimAsync(accountId, cancel);
        if (account is null)
        {
            return SyncReport.NotStarted(accountId, refusal ?? "The account cannot be synchronised.");
        }

        var run = new SyncRun(account, _options);
        Exception? failure = null;
        try
        {
            // Everything of the run happens inside the account's tenant: the database refuses to touch another tenant's rows.
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystemInTenant(account.TenantId);
            IAccountSync sync = account.ReceiveProtocol == ReceiveProtocol.Pop3
                ? scope.ServiceProvider.GetRequiredService<Pop3AccountSync>()
                : scope.ServiceProvider.GetRequiredService<ImapAccountSync>();
            await sync.RunAsync(run, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            run.Interrupted = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return await FinishAsync(account, run, failure, watch.Elapsed);
    }

    /// <summary>
    /// Loads the account (as it was before this run) and marks it as running, unless another run holds it. The returned account
    /// still carries the previous state and message.
    /// </summary>
    private async Task<(MailAccount? Account, string? Refusal)> ClaimAsync(long accountId, CancellationToken cancel)
    {
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        MailAccount? account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancel);
        if (account is null)
        {
            return (null, "The account does not exist.");
        }

        string? refusal = RefusalOf(account);
        if (refusal is null && !await db.Tenants.AnyAsync(t => t.Id == account.TenantId && t.IsActive, cancel))
        {
            refusal = "The tenant of the account is deactivated.";
        }

        if (refusal is not null)
        {
            return (null, refusal);
        }

        DateTime now = DateTime.UtcNow;
        DateTime staleBefore = now - _options.StaleRunAfter;
        int claimed = await db.MailAccounts
            .Where(a => a.Id == accountId && (a.LastSyncState != SyncState.Running || a.UpdateDate < staleBefore))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastSyncState, SyncState.Running)
                .SetProperty(a => a.UpdateDate, now), cancel);
        return claimed == 1 ? (account, null) : (null, "A synchronisation of this account is already running.");
    }

    private async Task<SyncReport> FinishAsync(MailAccount account, SyncRun run, Exception? failure, TimeSpan duration)
    {
        DateTime now = DateTime.UtcNow;
        bool succeeded = failure is null && run.FolderErrors.Count == 0;
        bool failedCompletely = failure is not null || (run.FolderErrors.Count > 0 && run.FoldersSynced == 0);
        string summary = SyncText.Summary(run, _options.MaxMessageAttempts);
        string problem = SyncText.Problem(run, failure, account.ReceiveHost);
        string message = succeeded ? summary : run.Processed > 0 || run.DeletedAtProvider > 0 ? $"{problem} ({summary})" : problem;
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        int failureCount = succeeded ? 0 : account.FailureCount + 1;
        DateTime next = SyncSchedule.NextSyncDate(now, SyncSchedule.IntervalOf(account), failedCompletely, failureCount, run.MorePending || run.Interrupted, _options.MaxBackoff);

        if (failure is not null)
        {
            _logger.LogWarning(failure is SyncConfigurationException ? null : failure, "Synchronisation of account {AccountId} failed: {Problem}", account.Id, problem);
        }

        await SaveStateAsync(account.Id, succeeded ? SyncState.Ok : SyncState.Error, now, message, failureCount, next);
        await LogActivityAsync(account, run, succeeded, problem, message, summary);

        return new SyncReport
        {
            AccountId = account.Id,
            Started = true,
            Succeeded = succeeded,
            Downloaded = run.Downloaded,
            DeletedAtProvider = run.DeletedAtProvider,
            Skipped = run.AlreadyPresent + run.TooLarge,
            Failed = run.Failed + run.GivenUp,
            FlagChanges = run.FlagChanges,
            MorePending = run.MorePending,
            Duration = duration,
            Message = message,
        };
    }

    private async Task SaveStateAsync(long accountId, SyncState state, DateTime now, string message, int failureCount, DateTime next)
    {
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastSyncState, state)
                .SetProperty(a => a.LastSyncDate, (DateTime?)now)
                .SetProperty(a => a.LastSyncMessage, message)
                .SetProperty(a => a.FailureCount, failureCount)
                .SetProperty(a => a.NextSyncDate, (DateTime?)next)
                .SetProperty(a => a.UpdateDate, now));
        }
        catch (Exception ex)
        {
            // The account stays "running" and is picked up again once that counts as stale.
            _logger.LogError(ex, "The synchronisation state of account {AccountId} could not be saved.", accountId);
        }
    }

    /// <summary>One line per run that did something or failed; an error that is the same as last time is not logged again.</summary>
    private async Task LogActivityAsync(MailAccount account, SyncRun run, bool succeeded, string problem, string message, string summary)
    {
        bool didSomething = run.Downloaded > 0 || run.DeletedAtProvider > 0 || run.RemovedLocally > 0;
        if (!succeeded)
        {
            bool repeated = account.LastSyncState == SyncState.Error && (account.LastSyncMessage ?? string.Empty).StartsWith(problem, StringComparison.Ordinal);
            if (!repeated)
            {
                await _activity.ErrorAsync(ActivityCategory.Sync, $"{account.Name}: {message}", tenantId: account.TenantId);
            }
            else if (didSomething)
            {
                await _activity.InfoAsync(ActivityCategory.Sync, $"{account.Name}: {summary}", tenantId: account.TenantId);
            }

            return;
        }

        if (didSomething)
        {
            await _activity.InfoAsync(ActivityCategory.Sync, $"{account.Name}: {message}", tenantId: account.TenantId);
        }
        else if (account.LastSyncState == SyncState.Error)
        {
            await _activity.InfoAsync(ActivityCategory.Sync, $"{account.Name}: the synchronisation works again ({message})", tenantId: account.TenantId);
        }
    }
}

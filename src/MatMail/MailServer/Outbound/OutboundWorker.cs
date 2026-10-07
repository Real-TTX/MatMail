using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailServer.Outbound;

/// <summary>Tunables of the worker; tests point direct delivery at a local port.</summary>
internal sealed class OutboundWorkerOptions
{
    public int MaxParallel { get; init; } = 4;
    public int DirectPort { get; init; } = 25;
    public TimeSpan DirectTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The longest one delivery attempt may take; must stay well below <see cref="OutboundWorker.ClaimLease"/>.</summary>
    public TimeSpan MaxAttemptDuration { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The outgoing delivery worker: sends the queued <see cref="OutboundMessage"/>s that are due — up to four at a time, each in its
/// own scope as the system — through the provider account they are routed to, or directly to the recipients' mail servers.
/// It wakes up when something is queued (<see cref="OutboundSignal"/>) or a delivery slot becomes free, and otherwise looks every
/// 30 seconds; a slow receiving server only ever holds its own slot. Once an hour it removes old entries.
/// <para>
/// Claiming is a single UPDATE … RETURNING with FOR UPDATE SKIP LOCKED, so two workers (or two installations on one database)
/// never send the same message. A claimed entry is "Sending" with <see cref="OutboundMessage.NextAttemptDate"/> as the end of
/// its lease: if the worker dies, the entry is taken up again after <see cref="ClaimLease"/>. An attempt is cut off long before
/// (<see cref="OutboundWorkerOptions.MaxAttemptDuration"/>), and its outcome is only saved while the lease still holds.
/// </para>
/// </summary>
public sealed class OutboundWorker : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HousekeepingInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan ClaimLease = TimeSpan.FromHours(1);

    /// <summary>Failed entries stay this long, so the queue view can show what went wrong.</summary>
    public const int FailedRetentionDays = 30;

    private static readonly TimeSpan RetryAfterInternalError = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly OutboundSignal _signal;
    private readonly IMxResolver _mx;
    private readonly ILogger<OutboundWorker> _logger;
    private readonly OutboundWorkerOptions _options;

    public OutboundWorker(IServiceScopeFactory scopes, AppConfig config, OutboundSignal signal, IMxResolver mx, ILogger<OutboundWorker> logger)
        : this(scopes, config, signal, mx, logger, new OutboundWorkerOptions())
    {
    }

    internal OutboundWorker(
        IServiceScopeFactory scopes, AppConfig config, OutboundSignal signal, IMxResolver mx, ILogger<OutboundWorker> logger, OutboundWorkerOptions options)
    {
        _scopes = scopes;
        _config = config;
        _signal = signal;
        _mx = mx;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Queue.Enabled)
        {
            _logger.LogInformation("The outgoing queue is switched off.");
            return;
        }

        var running = new List<Task>();
        DateTime nextHousekeeping = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                running.RemoveAll(task => task.IsCompleted);
                await StartDueAsync(running, stoppingToken);
                if (DateTime.UtcNow >= nextHousekeeping)
                {
                    await CleanUpAsync(stoppingToken);
                    nextHousekeeping = DateTime.UtcNow + HousekeepingInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The outgoing queue failed; trying again shortly.");
            }

            try
            {
                await _signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Deliveries that are running record what they achieved and end.
        await Task.WhenAll(running);
    }

    /// <summary>Claims due entries while delivery slots are free; each one runs on its own and frees its slot when done.</summary>
    private async Task StartDueAsync(List<Task> running, CancellationToken stopping)
    {
        await ReleaseExpiredClaimsAsync(stopping);
        while (running.Count(task => !task.IsCompleted) < Math.Max(1, _options.MaxParallel))
        {
            long? id = await ClaimNextAsync(stopping);
            if (id is null)
            {
                return;
            }

            running.Add(RunInSlotAsync(id.Value, stopping));
        }
    }

    private async Task RunInSlotAsync(long id, CancellationToken stopping)
    {
        try
        {
            await ProcessAsync(id, stopping);
        }
        catch (Exception ex)
        {
            // ProcessAsync handles its own failures; whatever still escapes must not end the worker.
            _logger.LogError(ex, "Queue entry {Id} could not be processed.", id);
        }
        finally
        {
            // A slot is free: look for more work at once instead of waiting for the next poll.
            _signal.Notify();
        }
    }

    /// <summary>
    /// Sends every entry that is due (several at a time), waits until all are done and returns how many were worked on — for a
    /// "send now" action and the tests. The background service itself uses delivery slots and never waits for a slow one.
    /// </summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancel = default)
    {
        await ReleaseExpiredClaimsAsync(cancel);

        int processed = 0;
        async Task LaneAsync()
        {
            while (!cancel.IsCancellationRequested)
            {
                long? id = await ClaimNextAsync(cancel);
                if (id is null)
                {
                    return;
                }

                await ProcessAsync(id.Value, cancel);
                Interlocked.Increment(ref processed);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.MaxParallel)).Select(_ => LaneAsync()));
        return processed;
    }

    /// <summary>
    /// Removes old entries: sent and cancelled ones after <see cref="RetentionConfig.SentQueueDays"/> (0 or less keeps them),
    /// failed ones after <see cref="FailedRetentionDays"/>. Returns how many were removed.
    /// </summary>
    public async Task<int> CleanUpAsync(CancellationToken cancel = default)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        DateTime now = DateTime.UtcNow;
        int removed = 0;
        int sentDays = _config.Retention.SentQueueDays;
        if (sentDays > 0)
        {
            DateTime sentBefore = now.AddDays(-sentDays);
            removed += await db.OutboundMessages.IgnoreQueryFilters()
                .Where(o => (o.Status == OutboundStatus.Sent && (o.SentDate ?? o.UpdateDate) < sentBefore)
                            || (o.Status == OutboundStatus.Cancelled && o.UpdateDate < sentBefore))
                .ExecuteDeleteAsync(cancel);
        }

        DateTime failedBefore = now.AddDays(-FailedRetentionDays);
        removed += await db.OutboundMessages.IgnoreQueryFilters()
            .Where(o => o.Status == OutboundStatus.Failed && o.UpdateDate < failedBefore)
            .ExecuteDeleteAsync(cancel);

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} old entries from the outgoing queue.", removed);
        }

        return removed;
    }

    /// <summary>Takes the next due entry (Pending → Sending) in one statement; null when nothing is due.</summary>
    private async Task<long?> ClaimNextAsync(CancellationToken cancel)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        DateTime now = DateTime.UtcNow;
        DateTime leaseEnd = now + ClaimLease;
        string sending = nameof(OutboundStatus.Sending);
        string pending = nameof(OutboundStatus.Pending);
        List<long> claimed = await db.Database.SqlQuery<long>($"""
            UPDATE "OutboundMessage" SET "Status" = {sending}, "NextAttemptDate" = {leaseEnd}, "UpdateDate" = {now}
            WHERE "Id" = (
                SELECT "Id" FROM "OutboundMessage"
                WHERE "Status" = {pending} AND "NextAttemptDate" <= {now}
                ORDER BY "NextAttemptDate", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancel);
        return claimed.Count == 0 ? null : claimed[0];
    }

    /// <summary>Entries whose worker died while sending: back into the queue once their lease is over.</summary>
    private async Task ReleaseExpiredClaimsAsync(CancellationToken cancel)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        DateTime now = DateTime.UtcNow;
        int released = await db.OutboundMessages.IgnoreQueryFilters()
            .Where(o => o.Status == OutboundStatus.Sending && o.NextAttemptDate < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OutboundStatus.Pending)
                .SetProperty(o => o.UpdateDate, now), cancel);
        if (released > 0)
        {
            _logger.LogWarning("{Count} outgoing messages were left in 'Sending' and are queued again.", released);
        }
    }

    private async Task ProcessAsync(long id, CancellationToken cancel)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            await new OutboundDelivery(scope.ServiceProvider, _mx, _options, _logger).ProcessAsync(id, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Shut down before the attempt began: the entry is due again right after the restart.
            await ReturnToQueueAsync(id, null, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            // A claimed entry is never left behind: it goes back into the queue for a later attempt.
            _logger.LogError(ex, "Queue entry {Id} could not be processed.", id);
            await ReturnToQueueAsync(id, ex.Message, RetryAfterInternalError);
        }
    }

    private async Task ReturnToQueueAsync(long id, string? error, TimeSpan delay)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            DateTime now = DateTime.UtcNow;
            DateTime next = now + delay;
            await db.OutboundMessages.IgnoreQueryFilters()
                .Where(o => o.Id == id && o.Status == OutboundStatus.Sending)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(o => o.Status, OutboundStatus.Pending);
                    s.SetProperty(o => o.NextAttemptDate, next);
                    s.SetProperty(o => o.UpdateDate, now);
                    if (error is not null)
                    {
                        s.SetProperty(o => o.LastError, error);
                    }
                });
        }
        catch (Exception ex)
        {
            // The lease brings it back later.
            _logger.LogError(ex, "Queue entry {Id} could not be put back into the queue.", id);
        }
    }
}

using System.Threading.Channels;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Push;

/// <summary>
/// Tells the devices of the users when mail arrives (Web Push). It listens to the mail store like the live updates of the web client;
/// what arrives within a moment is collected, so a sync that brings fifty messages sends one notification, not fifty.
/// </summary>
public sealed class PushNotifier : BackgroundService
{
    /// <summary>How long a message waits for others that arrive with it.</summary>
    public static readonly TimeSpan DefaultCollect = TimeSpan.FromSeconds(2);

    /// <summary>After this many failures in a row a subscription is dropped (a success sets the count back to zero).</summary>
    public const int MaxFailures = 10;

    private readonly MailEventHub _hub;
    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly ILogger<PushNotifier> _logger;
    private readonly TimeSpan _collect;
    private readonly Channel<MailEvent> _events = Channel.CreateBounded<MailEvent>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    public PushNotifier(MailEventHub hub, IServiceScopeFactory scopes, AppConfig config, ILogger<PushNotifier> logger, TimeSpan? collect = null)
    {
        _hub = hub;
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _collect = collect ?? DefaultCollect;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Push.Enabled)
        {
            return;
        }

        using IDisposable subscription = _hub.Subscribe(e =>
        {
            if (e is { Kind: MailEventKind.NewMessage, MessageId: not null })
            {
                _events.Writer.TryWrite(e);
            }
        });

        try
        {
            while (await _events.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(_collect, stoppingToken);
                var batch = new List<MailEvent>();
                while (batch.Count < 1000 && _events.Reader.TryRead(out MailEvent? next))
                {
                    batch.Add(next);
                }

                try
                {
                    await NotifyAsync(batch, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Notifications could not be sent.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Plans and sends the notifications for a batch of arrivals and keeps the subscriptions up to date.</summary>
    public async Task NotifyAsync(IReadOnlyCollection<MailEvent> events, CancellationToken cancel)
    {
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();
        var planner = scope.ServiceProvider.GetRequiredService<PushPlanner>();

        IReadOnlyList<PlannedPush> plans = await planner.PlanAsync(events, cancel);
        if (plans.Count == 0)
        {
            return;
        }

        // A few at a time: the push services answer quickly, and one slow one does not hold up the others.
        var outcomes = new Dictionary<long, PushOutcome>();
        var gate = new object();
        await Parallel.ForEachAsync(plans, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancel }, async (plan, token) =>
        {
            PushOutcome outcome = await sender.SendAsync(plan.Subscription, plan.Message, new PushOptions(Topic: plan.Topic), token);
            lock (gate)
            {
                outcomes[plan.Subscription.Id] = outcome;
            }
        });

        await PushService.RecordOutcomesAsync(db, outcomes, MaxFailures, cancel);
    }
}

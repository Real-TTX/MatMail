using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.MailServer.Outbound;

/// <summary>The outcome of one attempt, sorted: delivered, deferred (tried again later) and bounced (given up).</summary>
internal sealed record OutboundAttempt(
    IReadOnlyList<RecipientOutcome> Delivered,
    IReadOnlyList<RecipientOutcome> Deferred,
    IReadOnlyList<RecipientOutcome> Bounced,
    bool Expired);

/// <summary>
/// The rules for a queue entry after an attempt: delivered recipients leave the entry (a later attempt never sends to them again),
/// permanently refused ones are bounced, temporarily failed ones are retried along <see cref="QueueConfig.RetryMinutes"/> until
/// <see cref="QueueConfig.MaxAgeHours"/>, then bounced as well.
/// </summary>
internal static class OutboundSchedule
{
    private const int MaxErrorLength = 4000;

    /// <summary>The wait after the <paramref name="attempts"/>-th attempt: RetryMinutes[attempts - 1]; the last entry repeats.</summary>
    public static TimeSpan RetryDelay(int attempts, int[]? retryMinutes)
    {
        if (retryMinutes is null || retryMinutes.Length == 0)
        {
            return TimeSpan.FromMinutes(15);
        }

        int index = Math.Clamp(attempts - 1, 0, retryMinutes.Length - 1);
        return TimeSpan.FromMinutes(Math.Max(1, retryMinutes[index]));
    }

    /// <summary>Records an attempt on the entry (status, recipients, next attempt, last error) and returns what happened.</summary>
    public static OutboundAttempt Apply(OutboundMessage message, IReadOnlyList<RecipientOutcome> outcomes, DateTime now, QueueConfig queue)
    {
        message.AttemptCount++;
        List<RecipientOutcome> delivered = outcomes.Where(o => o.State == RecipientState.Delivered).ToList();
        List<RecipientOutcome> permanent = outcomes.Where(o => o.State == RecipientState.PermanentFailure).ToList();
        List<RecipientOutcome> temporary = outcomes.Where(o => o.State == RecipientState.TemporaryFailure).ToList();
        bool expired = temporary.Count > 0 && now - message.CreateDate >= TimeSpan.FromHours(Math.Max(1, queue.MaxAgeHours));

        if (temporary.Count > 0 && !expired)
        {
            message.Status = OutboundStatus.Pending;
            message.Recipients = temporary.Select(o => o.Address).ToArray();
            message.NextAttemptDate = now + RetryDelay(message.AttemptCount, queue.RetryMinutes);
            message.LastError = Summarize(temporary.Concat(permanent));
            return new OutboundAttempt(delivered, temporary, permanent, false);
        }

        List<RecipientOutcome> bounced = expired ? permanent.Concat(temporary).ToList() : permanent;
        message.NextAttemptDate = now;
        if (delivered.Count > 0 || bounced.Count == 0)
        {
            message.Status = OutboundStatus.Sent;
            message.SentDate = now;
            if (delivered.Count > 0)
            {
                message.Recipients = delivered.Select(o => o.Address).ToArray();
            }

            message.LastError = bounced.Count == 0 ? null : Summarize(bounced);
        }
        else
        {
            // Kept with the failed recipients, so "retry" in the queue view tries exactly those again.
            message.Status = OutboundStatus.Failed;
            message.Recipients = bounced.Select(o => o.Address).ToArray();
            message.LastError = (expired ? $"Given up after {queue.MaxAgeHours} hours. " : string.Empty) + Summarize(bounced);
        }

        return new OutboundAttempt(delivered, Array.Empty<RecipientOutcome>(), bounced, expired);
    }

    public static string Summarize(IEnumerable<RecipientOutcome> outcomes)
    {
        string text = string.Join("\n", outcomes.Select(o => $"{o.Address}: {o.Detail}"));
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }
}

/// <summary>
/// Works on one claimed queue entry (status Sending): sends it, records the outcome, schedules the next attempt, delivers bounces
/// into the sender's mailbox and writes the activity log. Runs as the system in a scope of its own.
/// </summary>
internal sealed class OutboundDelivery
{
    private readonly MatMailDbContext _db;
    private readonly MailDelivery _delivery;
    private readonly ActivityLogger _log;
    private readonly AppConfig _config;
    private readonly OutboundTransport _transport;
    private readonly ILogger _logger;

    public OutboundDelivery(IServiceProvider services, IMxResolver mx, OutboundWorkerOptions options, ILogger logger)
    {
        _db = services.GetRequiredService<MatMailDbContext>();
        _delivery = services.GetRequiredService<MailDelivery>();
        _log = services.GetRequiredService<ActivityLogger>();
        _config = services.GetRequiredService<AppConfig>();
        _transport = new OutboundTransport(services.GetRequiredService<ProviderConnector>(), _config, mx, options);
        _logger = logger;
    }

    public async Task ProcessAsync(long id, CancellationToken cancel)
    {
        // System work across tenants: the queue is not filtered by tenant on purpose.
        OutboundMessage? message = await _db.OutboundMessages.IgnoreQueryFilters()
            .Include(o => o.MailAccount)
            .FirstOrDefaultAsync(o => o.Id == id, cancel);
        if (message is null || message.Status != OutboundStatus.Sending)
        {
            return;
        }

        List<string> recipients = message.Recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<RecipientOutcome> outcomes;
        try
        {
            outcomes = recipients.Count == 0 ? Array.Empty<RecipientOutcome>() : await _transport.SendAsync(message, LoadForSending(message.Raw), recipients, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Shutting down in the middle: the entry goes back into the queue without counting the attempt.
            message.Status = OutboundStatus.Pending;
            message.NextAttemptDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (FormatException ex)
        {
            outcomes = OutboundTransport.Fail(recipients, RecipientState.PermanentFailure, $"The queued message cannot be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending queue entry {Id} failed.", id);
            outcomes = OutboundTransport.Fail(recipients, RecipientState.TemporaryFailure, ex.Message);
        }

        // Every recipient gets an outcome; one the transport did not mention is tried again.
        var missing = recipients.Where(r => !outcomes.Any(o => string.Equals(o.Address, r, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            outcomes = outcomes.Concat(OutboundTransport.Fail(missing, RecipientState.TemporaryFailure, "No answer for this recipient.")).ToList();
        }

        OutboundAttempt attempt = OutboundSchedule.Apply(message, outcomes, DateTime.UtcNow, _config.Queue);
        await _db.SaveChangesAsync(CancellationToken.None);

        bool bounceDelivered = attempt.Bounced.Count > 0 && await DeliverBounceAsync(message, attempt);
        await LogAsync(message, attempt, bounceDelivered);
    }

    /// <summary>The message as stored, without Return-Path: that is set by whoever delivers it in the end (RFC 5321 4.4).</summary>
    private static MimeMessage LoadForSending(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        MimeMessage message = MimeMessage.Load(ParserOptions.Default, stream);
        message.Headers.RemoveAll(HeaderId.ReturnPath);
        return message;
    }

    /// <summary>
    /// Delivers a bounce into the sender's local mailbox. Nothing for external senders (they are not ours to inform) and never for
    /// the null sender, so a bounce is never bounced.
    /// </summary>
    private async Task<bool> DeliverBounceAsync(OutboundMessage message, OutboundAttempt attempt)
    {
        string sender = MailAddresses.Normalize(message.EnvelopeFrom);
        if (sender.Length == 0)
        {
            return false;
        }

        try
        {
            if (await _delivery.ResolveAsync(sender, message.TenantId) is null)
            {
                return false;
            }

            byte[] bounce = BounceMessage.Build(_config.Server.Hostname, message, attempt.Bounced, attempt.Expired, _config.Queue.MaxAgeHours);
            DeliveryResult result = await _delivery.DeliverAsync(bounce, new DeliverySource { EnvelopeRecipients = new[] { sender }, TenantId = message.TenantId });
            return result.Copies.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The bounce for queue entry {Id} could not be delivered.", message.Id);
            return false;
        }
    }

    private async Task LogAsync(OutboundMessage message, OutboundAttempt attempt, bool bounceDelivered)
    {
        string subject = message.Subject.Length > 120 ? message.Subject[..120] + "…" : message.Subject;
        string sender = message.EnvelopeFrom.Length == 0 ? "<>" : message.EnvelopeFrom;
        string route = OutboundTransport.DescribeRoute(message);

        if (attempt.Delivered.Count > 0)
        {
            await _log.InfoAsync(
                ActivityCategory.Queue,
                $"Sent '{subject}' from {sender} to {List(attempt.Delivered)} via {route}.",
                tenantId: message.TenantId,
                userId: message.SenderUserId);
        }

        if (attempt.Deferred.Count > 0)
        {
            await _log.WarnAsync(
                ActivityCategory.Queue,
                $"Delivery of '{subject}' from {sender} to {List(attempt.Deferred)} via {route} was deferred (attempt {message.AttemptCount}); next attempt {message.NextAttemptDate:yyyy-MM-dd HH:mm} UTC.",
                OutboundSchedule.Summarize(attempt.Deferred),
                message.TenantId,
                message.SenderUserId);
        }

        if (attempt.Bounced.Count > 0)
        {
            string why = attempt.Expired ? $" within {_config.Queue.MaxAgeHours} hours" : string.Empty;
            string bounce = bounceDelivered ? " A bounce was delivered to the sender." : string.Empty;
            await _log.ErrorAsync(
                ActivityCategory.Queue,
                $"'{subject}' from {sender} could not be delivered to {List(attempt.Bounced)}{why}.{bounce}",
                OutboundSchedule.Summarize(attempt.Bounced),
                message.TenantId,
                message.SenderUserId);
        }
    }

    private static string List(IReadOnlyList<RecipientOutcome> outcomes)
        => outcomes.Count <= 3
            ? string.Join(", ", outcomes.Select(o => o.Address))
            : string.Join(", ", outcomes.Take(3).Select(o => o.Address)) + $" and {outcomes.Count - 3} more";
}

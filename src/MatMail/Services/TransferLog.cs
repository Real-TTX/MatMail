using MatMail.Configuration;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// Writes the mail transfer log (Administration → Mail transfers): one line per message that comes in, goes out or moves between mailboxes.
/// Like <see cref="ActivityLogger"/> it uses a scope of its own and the system actor, so it works from every kind of background service
/// and never takes a message down with it: a line that cannot be written is only noted in the application log.
/// </summary>
public sealed class TransferLog
{
    private const int MaxRecipientsText = 2000;

    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly ILogger<TransferLog> _logger;

    public TransferLog(IServiceScopeFactory scopes, AppConfig config, ILogger<TransferLog> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    /// <summary>False when the log is switched off (<see cref="RetentionConfig.TransferLogDays"/> is 0).</summary>
    public bool Enabled => _config.Retention.TransferLogDays > 0;

    /// <summary>Writes a line and returns its id (null when the log is off or the line could not be written).</summary>
    public async Task<long?> RecordAsync(
        TransferDirection direction,
        TransferChannel channel,
        TransferStatus status,
        string? sender,
        IReadOnlyCollection<string> recipients,
        string? subject,
        long sizeBytes,
        long? tenantId = null,
        string? messageId = null,
        string? peer = null,
        string? remoteIp = null,
        string? detail = null,
        long? outboundMessageId = null)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var line = new MailTransfer
            {
                TenantId = tenantId,
                Direction = direction,
                Channel = channel,
                Status = status,
                MessageIdHeader = Truncate(messageId, 500),
                Subject = _config.Retention.TransferLogSubjects ? Truncate(subject, 1000) ?? string.Empty : string.Empty,
                Sender = Truncate(sender, 320) ?? string.Empty,
                Recipients = Truncate(string.Join(", ", recipients), MaxRecipientsText) ?? string.Empty,
                RecipientCount = recipients.Count,
                SizeBytes = sizeBytes,
                Peer = Truncate(peer, 300),
                RemoteIp = Truncate(remoteIp, 64),
                Detail = Truncate(detail, 4000),
                OutboundMessageId = outboundMessageId,
                Attempts = 0,
            };
            db.MailTransfers.Add(line);
            await db.SaveChangesAsync();
            return line.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A line of the mail transfer log could not be written.");
            return null;
        }
    }

    /// <summary>Brings the line of an outgoing message up to date after a delivery attempt.</summary>
    public async Task UpdateOutboundAsync(long outboundMessageId, TransferStatus status, int attempts, string? detail)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            MailTransfer? line = await db.MailTransfers
                .Where(t => t.OutboundMessageId == outboundMessageId && t.Direction == TransferDirection.Outbound)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync();
            if (line is null)
            {
                return;
            }

            line.Status = status;
            line.Attempts = attempts;
            line.Detail = Truncate(detail, 4000);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The mail transfer log could not be brought up to date.");
        }
    }

    /// <summary>Removes the lines older than <see cref="RetentionConfig.TransferLogDays"/> (nothing when the log is off: what is there stays until it is switched on or cleaned by hand).</summary>
    public static Task<int> PruneAsync(MatMailDbContext db, int days, DateTime now, CancellationToken cancel)
        => days <= 0 ? Task.FromResult(0) : db.MailTransfers.Where(t => t.CreateDate < now.AddDays(-days)).ExecuteDeleteAsync(cancel);

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

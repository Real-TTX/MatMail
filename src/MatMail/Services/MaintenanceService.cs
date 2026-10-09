using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// Housekeeping once an hour: expired sessions, old activity-log entries, aged messages in Trash and Junk, forgotten uploads.
/// (The outgoing queue cleans up after itself in its own worker.)
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly AttachmentStaging _staging;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(IServiceScopeFactory scopes, AppConfig config, AttachmentStaging staging, ILogger<MaintenanceService> logger)
    {
        _scopes = scopes;
        _config = config;
        _staging = staging;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not right at the start: the database may still be coming up and the first requests matter more.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The housekeeping run failed.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancel)
    {
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        DateTime now = DateTime.UtcNow;

        int sessions = await db.UserSessions.Where(s => s.ExpiresDate < now).ExecuteDeleteAsync(cancel);
        int logs = await db.ActivityLogs.Where(a => a.CreateDate < now.AddDays(-Math.Max(1, _config.Retention.ActivityLogDays))).ExecuteDeleteAsync(cancel);
        int trash = await DeleteAgedAsync(db, FolderKind.Trash, _config.Retention.TrashDays, now, cancel);
        int junk = await DeleteAgedAsync(db, FolderKind.Junk, _config.Retention.JunkDays, now, cancel);
        int uploads = _staging.CleanUp();
        int transfers = await TransferLog.PruneAsync(db, _config.Retention.TransferLogDays, now, cancel);

        if (sessions + logs + trash + junk + uploads + transfers > 0)
        {
            _logger.LogInformation(
                "Housekeeping: {Sessions} sessions, {Logs} log entries, {Transfers} transfer log lines, {Trash} trash and {Junk} spam messages, {Uploads} uploads removed.",
                sessions, logs, transfers, trash, junk, uploads);
        }
    }

    private static async Task<int> DeleteAgedAsync(MatMailDbContext db, FolderKind kind, int days, DateTime now, CancellationToken cancel)
    {
        if (days <= 0)
        {
            return 0;
        }

        DateTime cutoff = now.AddDays(-days);
        return await db.MailMessages.Where(m => m.Folder!.Kind == kind && m.UpdateDate < cutoff).ExecuteDeleteAsync(cancel);
    }
}

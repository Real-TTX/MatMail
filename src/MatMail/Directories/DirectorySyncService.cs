using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Directories;

/// <summary>
/// Compares the users of every directory with it every so often (<see cref="DirectoryProvisioner.SyncAsync"/>): somebody who left or was
/// disabled there loses their open sessions, and their IMAP and SMTP clients, within the hour at the latest – a sign-in asks the directory
/// anyway, so this is about what is already open.
/// </summary>
public sealed class DirectorySyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly ILogger<DirectorySyncService> _logger;

    public DirectorySyncService(IServiceScopeFactory scopes, AppConfig config, ILogger<DirectorySyncService> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not right at the start: the database may still be coming up.
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.Directories.SyncMinutes > 0)
                {
                    await RunDueAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The comparison with the directories failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Compares every active directory that was not compared for <see cref="DirectoriesConfig.SyncMinutes"/>.</summary>
    public async Task RunDueAsync(CancellationToken cancel)
    {
        DateTime limit = DateTime.UtcNow - TimeSpan.FromMinutes(_config.Directories.SyncMinutes);
        List<long> due;
        using (IServiceScope scope = _scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            due = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().DirectoryConnections
                .Where(d => d.IsActive && (d.LastSyncDate == null || d.LastSyncDate <= limit))
                .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(cancel);
        }

        foreach (long id in due)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                // a scope of its own for each: one that fails must not take the others' changes with it
                using IServiceScope scope = _scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
                var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
                DirectoryConnection? dir = await db.DirectoryConnections.FirstOrDefaultAsync(d => d.Id == id && d.IsActive, cancel);
                if (dir is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<DirectoryProvisioner>().SyncAsync(dir, cancel);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The comparison with directory {DirectoryId} failed.", id);
            }
        }
    }
}

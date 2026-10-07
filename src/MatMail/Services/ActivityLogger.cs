using MatMail.Data;

namespace MatMail.Services;

/// <summary>
/// Writes the operational log (shown under Administration → Activity log). It uses its own scope and the system actor,
/// so entries survive a failing request and background services can log without a user.
/// </summary>
public sealed class ActivityLogger
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ActivityLogger> _logger;

    public ActivityLogger(IServiceScopeFactory scopes, ILogger<ActivityLogger> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task LogAsync(
        ActivityCategory category,
        ActivityLevel level,
        string message,
        string? details = null,
        long? tenantId = null,
        long? userId = null,
        string? remoteIp = null)
    {
        _logger.Log(
            level switch { ActivityLevel.Error => LogLevel.Error, ActivityLevel.Warning => LogLevel.Warning, _ => LogLevel.Information },
            "[{Category}] {Message}{Details}",
            category,
            message,
            string.IsNullOrEmpty(details) ? string.Empty : " — " + details);

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.ActivityLogs.Add(new ActivityLog
            {
                Category = category,
                Level = level,
                Message = Truncate(message, 2000)!,
                Details = Truncate(details, 8000),
                TenantId = tenantId,
                UserId = userId,
                RemoteIp = remoteIp,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The activity log entry could not be stored.");
        }
    }

    public Task InfoAsync(ActivityCategory category, string message, string? details = null, long? tenantId = null, long? userId = null, string? remoteIp = null)
        => LogAsync(category, ActivityLevel.Info, message, details, tenantId, userId, remoteIp);

    public Task WarnAsync(ActivityCategory category, string message, string? details = null, long? tenantId = null, long? userId = null, string? remoteIp = null)
        => LogAsync(category, ActivityLevel.Warning, message, details, tenantId, userId, remoteIp);

    public Task ErrorAsync(ActivityCategory category, string message, string? details = null, long? tenantId = null, long? userId = null, string? remoteIp = null)
        => LogAsync(category, ActivityLevel.Error, message, details, tenantId, userId, remoteIp);

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max];
}

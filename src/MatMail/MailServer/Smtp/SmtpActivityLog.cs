using System.Collections.Concurrent;
using MatMail.Data;
using MatMail.Services;

namespace MatMail.MailServer.Smtp;

/// <summary>
/// Writes SMTP events (relay use, denials, blocked sign-ins, errors) to the activity log without flooding it: an event with the
/// same key is written at most once per <see cref="Interval"/>; repeats in between are counted and mentioned in the next entry.
/// </summary>
public sealed class SmtpActivityLog
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private const int MaxKeys = 10_000;

    private readonly ActivityLogger _log;
    private readonly ConcurrentDictionary<string, Counter> _recent = new(StringComparer.Ordinal);

    public SmtpActivityLog(ActivityLogger log) => _log = log;

    private sealed class Counter
    {
        public DateTime LastWritten;
        public int Suppressed;
    }

    public Task InfoAsync(string key, string message, long? tenantId = null, long? userId = null, string? remoteIp = null, string? details = null)
        => WriteAsync(key, ActivityLevel.Info, message, tenantId, userId, remoteIp, details);

    public Task WarnAsync(string key, string message, long? tenantId = null, long? userId = null, string? remoteIp = null, string? details = null)
        => WriteAsync(key, ActivityLevel.Warning, message, tenantId, userId, remoteIp, details);

    public Task ErrorAsync(string key, string message, long? tenantId = null, long? userId = null, string? remoteIp = null, string? details = null)
        => WriteAsync(key, ActivityLevel.Error, message, tenantId, userId, remoteIp, details);

    public Task WriteAsync(string key, ActivityLevel level, string message, long? tenantId = null, long? userId = null, string? remoteIp = null, string? details = null)
    {
        DateTime now = DateTime.UtcNow;
        if (_recent.Count > MaxKeys)
        {
            Prune(now);
        }

        Counter counter = _recent.GetOrAdd(key, _ => new Counter());
        int suppressed;
        lock (counter)
        {
            if (counter.LastWritten > now - Interval)
            {
                counter.Suppressed++;
                return Task.CompletedTask;
            }

            suppressed = counter.Suppressed;
            counter.Suppressed = 0;
            counter.LastWritten = now;
        }

        if (suppressed > 0)
        {
            message += $" ({suppressed} more like this since the last entry)";
        }

        return _log.LogAsync(ActivityCategory.Smtp, level, message, details, tenantId, userId, remoteIp);
    }

    private void Prune(DateTime now)
    {
        foreach ((string key, Counter counter) in _recent)
        {
            if (counter.LastWritten <= now - Interval)
            {
                _recent.TryRemove(new KeyValuePair<string, Counter>(key, counter));
            }
        }
    }
}

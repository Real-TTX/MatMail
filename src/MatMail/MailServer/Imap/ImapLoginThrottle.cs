using System.Collections.Concurrent;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Failed IMAP logins per remote address within a sliding window. Every failure is answered after a delay; after
/// <see cref="DropAfterFailures"/> failures the connection is closed, and an address with <see cref="BlockAfterFailures"/> recent
/// failures is refused without checking the password at all (the per-user lockout of the sign-in service applies as well).
/// </summary>
public sealed class ImapLoginThrottle
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new(StringComparer.Ordinal);

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    public int DropAfterFailures { get; set; } = 5;

    public int BlockAfterFailures { get; set; } = 10;

    public TimeSpan FailureDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Counts a failure and returns the number of failures of this address within the window.</summary>
    public int RecordFailure(string remoteIp)
    {
        DateTime now = DateTime.UtcNow;
        Queue<DateTime> failures = _failures.GetOrAdd(remoteIp, _ => new Queue<DateTime>());
        int count;
        lock (failures)
        {
            Prune(failures, now);
            failures.Enqueue(now);
            count = failures.Count;
        }

        if (_failures.Count > 10_000)
        {
            RemoveStale(now);
        }

        return count;
    }

    public int FailureCount(string remoteIp)
    {
        if (!_failures.TryGetValue(remoteIp, out Queue<DateTime>? failures))
        {
            return 0;
        }

        lock (failures)
        {
            Prune(failures, DateTime.UtcNow);
            return failures.Count;
        }
    }

    public bool IsBlocked(string remoteIp) => FailureCount(remoteIp) >= BlockAfterFailures;

    public void Reset(string remoteIp) => _failures.TryRemove(remoteIp, out _);

    private void Prune(Queue<DateTime> failures, DateTime now)
    {
        while (failures.Count > 0 && now - failures.Peek() > Window)
        {
            failures.Dequeue();
        }
    }

    private void RemoveStale(DateTime now)
    {
        foreach ((string address, Queue<DateTime> failures) in _failures)
        {
            lock (failures)
            {
                Prune(failures, now);
                if (failures.Count == 0)
                {
                    _failures.TryRemove(address, out _);
                }
            }
        }
    }
}

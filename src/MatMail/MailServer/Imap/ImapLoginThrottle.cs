using System.Collections.Concurrent;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Failed IMAP logins per remote address and login name within a sliding window. Every failure is answered after a delay; after
/// <see cref="DropAfterFailures"/> failures the connection is closed, and a login name with <see cref="BlockAfterFailures"/> recent
/// failures from an address is refused from that address without checking the password at all (the per-user lockout of the
/// sign-in service applies as well). Counting per name keeps one wrong password in somebody's mail program from locking out
/// everybody behind the same address (an office, a mobile network); an address that fails at many names is slowed down by
/// <see cref="PenaltyDelay"/> but never refused.
/// </summary>
public sealed class ImapLoginThrottle
{
    private const int MaxLoginKeyLength = 200;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new(StringComparer.Ordinal);

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    public int DropAfterFailures { get; set; } = 5;

    /// <summary>Failures of one login name from one address after which that name is refused from there.</summary>
    public int BlockAfterFailures { get; set; } = 10;

    /// <summary>Failures of one address over all login names after which each attempt of it waits <see cref="PenaltyDelay"/>.</summary>
    public int PenalizeAfterFailures { get; set; } = 30;

    public TimeSpan PenaltyDelay { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan FailureDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Counts a failure and returns the number of failures of this address and login name within the window.</summary>
    public int RecordFailure(string remoteIp, string login)
    {
        DateTime now = DateTime.UtcNow;
        int count = Enqueue(Key(remoteIp, login), now);
        Enqueue(Key(remoteIp, null), now);

        if (_failures.Count > 10_000)
        {
            RemoveStale(now);
        }

        return count;
    }

    public int FailureCount(string remoteIp, string login) => Count(Key(remoteIp, login));

    public bool IsBlocked(string remoteIp, string login) => FailureCount(remoteIp, login) >= BlockAfterFailures;

    /// <summary>How long an attempt from this address has to wait because it failed at many login names.</summary>
    public TimeSpan Penalty(string remoteIp) => Count(Key(remoteIp, null)) >= PenalizeAfterFailures ? PenaltyDelay : TimeSpan.Zero;

    public void Reset(string remoteIp)
    {
        string prefix = remoteIp + "|";
        foreach (string key in _failures.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _failures.TryRemove(key, out _);
        }
    }

    private static string Key(string remoteIp, string? login)
    {
        string name = (login ?? string.Empty).Trim().ToLowerInvariant();
        return remoteIp + "|" + (name.Length > MaxLoginKeyLength ? name[..MaxLoginKeyLength] : name);
    }

    private int Enqueue(string key, DateTime now)
    {
        Queue<DateTime> failures = _failures.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (failures)
        {
            Prune(failures, now);
            failures.Enqueue(now);
            return failures.Count;
        }
    }

    private int Count(string key)
    {
        if (!_failures.TryGetValue(key, out Queue<DateTime>? failures))
        {
            return 0;
        }

        lock (failures)
        {
            Prune(failures, DateTime.UtcNow);
            return failures.Count;
        }
    }

    private void Prune(Queue<DateTime> failures, DateTime now)
    {
        while (failures.Count > 0 && now - failures.Peek() > Window)
        {
            failures.Dequeue();
        }
    }

    private void RemoveStale(DateTime now)
    {
        foreach ((string key, Queue<DateTime> failures) in _failures)
        {
            lock (failures)
            {
                Prune(failures, now);
                if (failures.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }
}

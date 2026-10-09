using System.Collections.Concurrent;

namespace MatMail.Directories;

/// <summary>
/// Stops guessing at a directory through MatMail. A person the directory has but MatMail has no user for yet is asked about at every sign-in,
/// and every wrong password is a failed sign-in at the directory too, where the lockout of the directory counts it: whoever sprays wrong
/// passwords at the web client could lock the people out of their company accounts. So after <see cref="MaxWrongPasswords"/> wrong passwords
/// for one login name within <see cref="Window"/> MatMail does not ask the directory about that name any more until the window has moved on
/// (the same five as the lockout of MatMail's own users: a directory that locks at ten stays out of reach of this gateway), and a client that
/// fails <see cref="MaxFailuresPerClient"/> times in that time is kept away from the directories altogether. Somebody who has a user here is
/// protected by that user's lockout already. In memory only: a restart forgets it, like the throttles of the mail servers.
/// </summary>
public sealed class DirectoryAttemptLimiter
{
    /// <summary>Wrong passwords per login name.</summary>
    public const int MaxWrongPasswords = 5;

    /// <summary>Failures (wrong passwords and unknown names) per client over all login names.</summary>
    public const int MaxFailuresPerClient = 50;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private const int PruneEvery = 256;
    private const int MaxKeyLength = 200;

    private sealed class Failures
    {
        public readonly Queue<DateTime> Times = new();

        /// <summary>Taken out of the table by the pruning: whoever still holds it must look the entry up again.</summary>
        public bool Removed;
    }

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Failures> _failures = new();
    private int _operations;

    public DirectoryAttemptLimiter(TimeProvider time) => _time = time;

    /// <summary>Whether the directories are not to be asked about this login name (or by this client) for now.</summary>
    public bool IsBlocked(string login, string? client)
    {
        DateTime now = Now();
        return Count(LoginKey(login), now) >= MaxWrongPasswords || (client is not null && Count(ClientKey(client), now) >= MaxFailuresPerClient);
    }

    /// <summary>A person the directory has gave a wrong password.</summary>
    public void RecordWrongPassword(string login, string? client)
    {
        DateTime now = Now();
        Add(LoginKey(login), now);
        if (client is not null)
        {
            Add(ClientKey(client), now);
        }
    }

    /// <summary>No directory knew the name (a client that tries one name after the other is looking for the people of the company).</summary>
    public void RecordUnknown(string? client)
    {
        if (client is not null)
        {
            Add(ClientKey(client), Now());
        }
    }

    /// <summary>The person signed in: what was wrong before does not count any more.</summary>
    public void Forget(string login)
    {
        if (_failures.TryRemove(LoginKey(login), out Failures? failures))
        {
            lock (failures)
            {
                failures.Removed = true;
            }
        }
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static string LoginKey(string login)
    {
        string name = login.Trim().ToLowerInvariant();
        return "l|" + (name.Length > MaxKeyLength ? name[..MaxKeyLength] : name);
    }

    private static string ClientKey(string client) => "c|" + client;

    private int Count(string key, DateTime now)
    {
        if (!_failures.TryGetValue(key, out Failures? failures))
        {
            return 0;
        }

        lock (failures)
        {
            DropExpired(failures.Times, now);
            return failures.Times.Count;
        }
    }

    private void Add(string key, DateTime now)
    {
        if (Interlocked.Increment(ref _operations) % PruneEvery == 0)
        {
            Prune(now);
        }

        while (true)
        {
            Failures failures = _failures.GetOrAdd(key, _ => new Failures());
            lock (failures)
            {
                if (failures.Removed)
                {
                    continue;   // the pruning took it out between the lookup and the lock
                }

                DropExpired(failures.Times, now);
                failures.Times.Enqueue(now);
                return;
            }
        }
    }

    private static void DropExpired(Queue<DateTime> times, DateTime now)
    {
        while (times.Count > 0 && times.Peek() <= now - Window)
        {
            times.Dequeue();
        }
    }

    private void Prune(DateTime now)
    {
        foreach ((string key, Failures failures) in _failures)
        {
            lock (failures)
            {
                DropExpired(failures.Times, now);
                if (failures.Times.Count == 0)
                {
                    failures.Removed = true;
                    _failures.TryRemove(new KeyValuePair<string, Failures>(key, failures));
                }
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MatMail.MailServer.Smtp;

/// <summary>The key client limits are counted by: an IPv4 address as it is, an IPv6 address by its /64 network.</summary>
internal static class ClientKey
{
    /// <summary>A single IPv6 host usually owns a whole /64 and can change its address within it at will.</summary>
    public static IPAddress Of(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }
}

/// <summary>
/// Per-client throttling of SMTP sign-ins, on top of the per-user lockout of the sign-in service. A client (see <see cref="ClientKey"/>)
/// may fail <see cref="MaxFailures"/> times per login name within <see cref="Window"/> before it may not try that name for
/// <see cref="BlockDuration"/>; over all names it may fail <see cref="MaxClientFailures"/> times. So one wrong password that a mail
/// program keeps sending does not lock out everybody behind the same address (an office, a mobile network), while guessing at
/// many names still ends quickly. Attempts are reserved before the password is checked, so parallel connections cannot multiply
/// the guesses: failures plus attempts in flight never exceed a limit. A successful sign-in does not reset the count, so a known
/// account cannot be used to keep guessing others.
/// </summary>
public sealed class SmtpAuthThrottle
{
    /// <summary>Failures per client and login name.</summary>
    public const int MaxFailures = 5;

    /// <summary>Failures per client over all login names.</summary>
    public const int MaxClientFailures = 50;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(10);

    private const int PruneEvery = 256;
    private const int MaxLoginKeyLength = 200;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private int _operations;

    private sealed class Entry
    {
        public Entry(int limit) => Limit = limit;

        public readonly int Limit;
        public readonly Queue<DateTime> Failures = new();
        public DateTime BlockedUntil;
        public int InFlight;
    }

    /// <summary>Is the client blocked altogether (too many failures over all login names)?</summary>
    public bool IsBlocked(IPAddress address) => IsBlocked(address, DateTime.UtcNow);

    /// <summary>Is the client blocked for this login name, or altogether?</summary>
    public bool IsBlocked(IPAddress address, string login) => IsBlocked(address, login, DateTime.UtcNow);

    /// <summary>
    /// Reserves a sign-in attempt. False when the client is blocked, or when the attempts already running could use up the
    /// failures it has left; then it has to try again later.
    /// </summary>
    public bool TryBeginAttempt(IPAddress address, string login) => TryBeginAttempt(address, login, DateTime.UtcNow);

    /// <summary>Ends an attempt reserved with <see cref="TryBeginAttempt(IPAddress, string)"/>. Returns true when its failure blocks the client.</summary>
    public bool EndAttempt(IPAddress address, string login, bool failed) => EndAttempt(address, login, failed, DateTime.UtcNow);

    /// <summary>Counts a failed sign-in. Returns true when the client is blocked (for this name or altogether) because of this failure.</summary>
    public bool RecordFailure(IPAddress address, string login) => RecordFailure(address, login, DateTime.UtcNow);

    internal bool IsBlocked(IPAddress address, DateTime now)
    {
        if (!_entries.TryGetValue(Key(address, null), out Entry? client))
        {
            return false;
        }

        lock (client)
        {
            return client.BlockedUntil > now;
        }
    }

    internal bool IsBlocked(IPAddress address, string login, DateTime now)
    {
        if (IsBlocked(address, now))
        {
            return true;
        }

        if (!_entries.TryGetValue(Key(address, login), out Entry? pair))
        {
            return false;
        }

        lock (pair)
        {
            return pair.BlockedUntil > now;
        }
    }

    internal bool TryBeginAttempt(IPAddress address, string login, DateTime now)
    {
        PruneOccasionally(now);
        (Entry client, Entry pair) = Entries(address, login);
        lock (client)
        {
            lock (pair)
            {
                DropExpired(client, now);
                DropExpired(pair, now);
                if (client.BlockedUntil > now || pair.BlockedUntil > now
                    || client.Failures.Count + client.InFlight >= client.Limit || pair.Failures.Count + pair.InFlight >= pair.Limit)
                {
                    return false;
                }

                client.InFlight++;
                pair.InFlight++;
                return true;
            }
        }
    }

    internal bool EndAttempt(IPAddress address, string login, bool failed, DateTime now)
    {
        (Entry client, Entry pair) = Entries(address, login);
        lock (client)
        {
            lock (pair)
            {
                client.InFlight = Math.Max(0, client.InFlight - 1);
                pair.InFlight = Math.Max(0, pair.InFlight - 1);
            }
        }

        return failed && RecordFailure(address, login, now);
    }

    internal bool RecordFailure(IPAddress address, string login, DateTime now)
    {
        PruneOccasionally(now);
        (Entry client, Entry pair) = Entries(address, login);
        bool blocked = false;
        lock (client)
        {
            lock (pair)
            {
                blocked |= Count(client, now);
                blocked |= Count(pair, now);
            }
        }

        return blocked;
    }

    private static bool Count(Entry entry, DateTime now)
    {
        DropExpired(entry, now);
        entry.Failures.Enqueue(now);
        if (entry.Failures.Count < entry.Limit)
        {
            return false;
        }

        entry.Failures.Clear();
        entry.BlockedUntil = now + BlockDuration;
        return true;
    }

    private (Entry Client, Entry Pair) Entries(IPAddress address, string login)
        => (_entries.GetOrAdd(Key(address, null), _ => new Entry(MaxClientFailures)),
            _entries.GetOrAdd(Key(address, login), _ => new Entry(MaxFailures)));

    private static string Key(IPAddress address, string? login)
    {
        string name = (login ?? string.Empty).Trim().ToLowerInvariant();
        return ClientKey.Of(address) + "|" + (name.Length > MaxLoginKeyLength ? name[..MaxLoginKeyLength] : name);
    }

    private static void DropExpired(Entry entry, DateTime now)
    {
        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= now - Window)
        {
            entry.Failures.Dequeue();
        }
    }

    /// <summary>Forgets clients that neither failed recently, nor are blocked or signing in, so the table cannot grow without bound.</summary>
    private void PruneOccasionally(DateTime now)
    {
        if (Interlocked.Increment(ref _operations) % PruneEvery != 0)
        {
            return;
        }

        foreach ((string key, Entry entry) in _entries)
        {
            bool idle;
            lock (entry)
            {
                DropExpired(entry, now);
                idle = entry.Failures.Count == 0 && entry.BlockedUntil <= now && entry.InFlight == 0;
            }

            if (idle)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }
}

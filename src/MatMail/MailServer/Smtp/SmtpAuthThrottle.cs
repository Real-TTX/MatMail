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
/// Per-client throttling of SMTP sign-ins, on top of the per-user lockout of the sign-in service: after <see cref="MaxFailures"/>
/// failures within <see cref="Window"/> a client (see <see cref="ClientKey"/>) may not sign in for <see cref="BlockDuration"/>.
/// Attempts are reserved before the password is checked, so parallel connections cannot multiply the guesses: failures plus
/// attempts in flight never exceed the limit. A successful sign-in does not reset the count, so a known account cannot be used
/// to keep guessing others.
/// </summary>
public sealed class SmtpAuthThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(10);

    private const int PruneEvery = 256;

    private readonly ConcurrentDictionary<IPAddress, Entry> _entries = new();
    private int _operations;

    private sealed class Entry
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime BlockedUntil;
        public int InFlight;
    }

    public bool IsBlocked(IPAddress address) => IsBlocked(address, DateTime.UtcNow);

    /// <summary>
    /// Reserves a sign-in attempt. False when the client is blocked, or when the attempts already running could use up the
    /// failures it has left; then it has to try again later.
    /// </summary>
    public bool TryBeginAttempt(IPAddress address) => TryBeginAttempt(address, DateTime.UtcNow);

    /// <summary>Ends an attempt reserved with <see cref="TryBeginAttempt(IPAddress)"/>. Returns true when its failure blocks the client.</summary>
    public bool EndAttempt(IPAddress address, bool failed) => EndAttempt(address, failed, DateTime.UtcNow);

    /// <summary>Counts a failed sign-in. Returns true when the client is blocked because of this failure.</summary>
    public bool RecordFailure(IPAddress address) => RecordFailure(address, DateTime.UtcNow);

    internal bool IsBlocked(IPAddress address, DateTime now)
    {
        if (!_entries.TryGetValue(ClientKey.Of(address), out Entry? entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.BlockedUntil > now;
        }
    }

    internal bool TryBeginAttempt(IPAddress address, DateTime now)
    {
        PruneOccasionally(now);
        Entry entry = _entries.GetOrAdd(ClientKey.Of(address), _ => new Entry());
        lock (entry)
        {
            DropExpired(entry, now);
            if (entry.BlockedUntil > now || entry.Failures.Count + entry.InFlight >= MaxFailures)
            {
                return false;
            }

            entry.InFlight++;
            return true;
        }
    }

    internal bool EndAttempt(IPAddress address, bool failed, DateTime now)
    {
        Entry entry = _entries.GetOrAdd(ClientKey.Of(address), _ => new Entry());
        lock (entry)
        {
            entry.InFlight = Math.Max(0, entry.InFlight - 1);
        }

        return failed && RecordFailure(address, now);
    }

    internal bool RecordFailure(IPAddress address, DateTime now)
    {
        PruneOccasionally(now);
        Entry entry = _entries.GetOrAdd(ClientKey.Of(address), _ => new Entry());
        lock (entry)
        {
            DropExpired(entry, now);
            entry.Failures.Enqueue(now);
            if (entry.Failures.Count < MaxFailures)
            {
                return false;
            }

            entry.Failures.Clear();
            entry.BlockedUntil = now + BlockDuration;
            return true;
        }
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

        foreach ((IPAddress address, Entry entry) in _entries)
        {
            bool idle;
            lock (entry)
            {
                DropExpired(entry, now);
                idle = entry.Failures.Count == 0 && entry.BlockedUntil <= now && entry.InFlight == 0;
            }

            if (idle)
            {
                _entries.TryRemove(new KeyValuePair<IPAddress, Entry>(address, entry));
            }
        }
    }
}

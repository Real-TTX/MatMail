using System.Collections.Concurrent;
using System.Net;

namespace MatMail.MailServer.Smtp;

/// <summary>
/// Per-address throttling of failed SMTP sign-ins, on top of the per-user lockout of the sign-in service: after
/// <see cref="MaxFailures"/> failures within <see cref="Window"/> an address may not sign in for <see cref="BlockDuration"/>.
/// A successful sign-in does not reset the count, so a known account cannot be used to keep guessing others. IPv6 addresses
/// count per /64 network, because a single host usually owns a whole /64 and could change its address at will.
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
    }

    public bool IsBlocked(IPAddress address) => IsBlocked(address, DateTime.UtcNow);

    /// <summary>Counts a failed sign-in. Returns true when the address is blocked because of this failure.</summary>
    public bool RecordFailure(IPAddress address) => RecordFailure(address, DateTime.UtcNow);

    internal bool IsBlocked(IPAddress address, DateTime now)
    {
        if (!_entries.TryGetValue(KeyOf(address), out Entry? entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.BlockedUntil > now;
        }
    }

    internal bool RecordFailure(IPAddress address, DateTime now)
    {
        PruneOccasionally(now);
        Entry entry = _entries.GetOrAdd(KeyOf(address), _ => new Entry());
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

    /// <summary>IPv4 as it is; IPv6 reduced to its /64 network.</summary>
    private static IPAddress KeyOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address;
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    private static void DropExpired(Entry entry, DateTime now)
    {
        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= now - Window)
        {
            entry.Failures.Dequeue();
        }
    }

    /// <summary>Forgets addresses that neither failed recently nor are blocked, so the table cannot grow without bound.</summary>
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
                idle = entry.Failures.Count == 0 && entry.BlockedUntil <= now;
            }

            if (idle)
            {
                _entries.TryRemove(new KeyValuePair<IPAddress, Entry>(address, entry));
            }
        }
    }
}

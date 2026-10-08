using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Push;

/// <summary>The devices of a user that get notifications: subscribing, listing, removing.</summary>
public sealed class PushService(MatMailDbContext db, PushKeys keys, AppConfig config)
{
    /// <summary>Most devices a user may have; the oldest give way (a browser that was reset subscribes anew each time).</summary>
    public const int MaxDevicesPerUser = 20;

    public bool Enabled => config.Push.Enabled;

    /// <summary>What the browser needs to subscribe to this server.</summary>
    public string PublicKey => keys.PublicKey;

    /// <summary>
    /// Registers a device (or updates it: the address of a browser is unique, so a second user of the same browser takes it over).
    /// Returns an English message (the translation key) when the subscription cannot be used.
    /// </summary>
    public async Task<string?> SubscribeAsync(
        long userId, string? endpoint, string? p256dh, string? auth, string? userAgent, bool? ownMailboxOnly, string? replaces, CancellationToken cancel)
    {
        if (!Enabled)
        {
            return "Notifications are switched off on this server.";
        }

        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 1000 || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "This device cannot receive notifications.";
        }

        if (WebPushEncryption.FromBase64Url(p256dh) is not { Length: 65 } key || key[0] != 0x04 || WebPushEncryption.FromBase64Url(auth) is not { Length: 16 })
        {
            return "This device cannot receive notifications.";
        }

        PushSubscription? existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancel);
        if (existing is null)
        {
            existing = new PushSubscription { Endpoint = endpoint };
            db.PushSubscriptions.Add(existing);
        }

        // The browser replaced a subscription (it does that now and then): the choice of the person moves to the new one.
        PushSubscription? replaced = string.IsNullOrEmpty(replaces) || replaces == endpoint
            ? null
            : await db.PushSubscriptions.FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == replaces, cancel);

        existing.UserId = userId;
        existing.P256dh = p256dh!;
        existing.Auth = auth!;
        existing.DeviceName = UserAgentInfo.Describe(userAgent);
        existing.OwnMailboxOnly = ownMailboxOnly ?? replaced?.OwnMailboxOnly ?? existing.OwnMailboxOnly;
        existing.FailureCount = 0;
        if (replaced is not null)
        {
            db.PushSubscriptions.Remove(replaced);
        }

        await db.SaveChangesAsync(cancel);

        // Keep the list of a user short: the oldest devices go.
        List<long> surplus = await db.PushSubscriptions.Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdateDate).ThenByDescending(s => s.Id).Skip(MaxDevicesPerUser).Select(s => s.Id).ToListAsync(cancel);
        if (surplus.Count > 0)
        {
            await db.PushSubscriptions.Where(s => surplus.Contains(s.Id)).ExecuteDeleteAsync(cancel);
        }

        return null;
    }

    /// <summary>The device of the user with this address, if it is registered (the page asks after loading).</summary>
    public Task<PushSubscription?> FindAsync(long userId, string endpoint, CancellationToken cancel)
        => db.PushSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, cancel);

    public async Task<bool> UnsubscribeAsync(long userId, string endpoint, CancellationToken cancel)
        => await db.PushSubscriptions.Where(s => s.UserId == userId && s.Endpoint == endpoint).ExecuteDeleteAsync(cancel) > 0;

    public Task<List<PushSubscription>> ListAsync(long userId, CancellationToken cancel)
        => db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.CreateDate).ToListAsync(cancel);

    public async Task<bool> RemoveAsync(long userId, long id, CancellationToken cancel)
        => await db.PushSubscriptions.Where(s => s.UserId == userId && s.Id == id).ExecuteDeleteAsync(cancel) > 0;

    /// <summary>What the push services answered: a success clears the failures, a vanished subscription goes, a failing one goes after a while.</summary>
    public static async Task RecordOutcomesAsync(MatMailDbContext db, IReadOnlyDictionary<long, PushOutcome> outcomes, int maxFailures, CancellationToken cancel)
    {
        if (outcomes.Count == 0)
        {
            return;
        }

        long[] ids = outcomes.Keys.ToArray();
        List<PushSubscription> rows = await db.PushSubscriptions.Where(s => ids.Contains(s.Id)).ToListAsync(cancel);
        foreach (PushSubscription row in rows)
        {
            switch (outcomes[row.Id])
            {
                case PushOutcome.Delivered:
                    row.LastSuccessDate = DateTime.UtcNow;
                    row.FailureCount = 0;
                    break;
                case PushOutcome.Gone:
                    db.PushSubscriptions.Remove(row);
                    break;
                default:
                    row.FailureCount++;
                    if (row.FailureCount >= maxFailures)
                    {
                        db.PushSubscriptions.Remove(row);
                    }

                    break;
            }
        }

        await db.SaveChangesAsync(cancel);
    }
}

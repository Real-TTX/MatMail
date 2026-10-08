using System.Globalization;
using System.Text.Json;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Push;

/// <summary>What one device is told, and who it is for.</summary>
/// <param name="Subscription">The device.</param>
/// <param name="Message">The JSON the service worker of the page shows: title, body, the address to open and a tag.</param>
/// <param name="Topic">Replaces an undelivered notification of the same mailbox at the push service.</param>
public sealed record PlannedPush(PushSubscription Subscription, byte[] Message, string Topic);

/// <summary>
/// Decides who gets a notification for what arrived: the owner of a mailbox, and – for devices that asked for it – everyone who may
/// read it; only unread mail in an inbox. Several messages that arrive together become one notification per device.
/// </summary>
public sealed class PushPlanner(MatMailDbContext db, IStringLocalizerFactory localizers, AppConfig config)
{
    private const int MaxTitle = 80;
    private const int MaxBody = 160;

    /// <summary>
    /// Mail older than this is not news: a connected account that is synchronised for the first time delivers its whole history through
    /// the same door as a message that just arrived, and nobody wants a notification for last year's mail.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    private sealed record Arrival(long Id, long MailboxId, long FolderId, string FromName, string FromAddress, string Subject, DateTime ReceivedDate);

    public async Task<IReadOnlyList<PlannedPush>> PlanAsync(IReadOnlyCollection<MailEvent> events, CancellationToken cancel)
    {
        long[] ids = events.Where(e => e is { Kind: MailEventKind.NewMessage, MessageId: not null }).Select(e => e.MessageId!.Value).Distinct().ToArray();
        if (ids.Length == 0 || !await db.PushSubscriptions.AnyAsync(cancel))
        {
            return [];
        }

        DateTime oldest = DateTime.UtcNow - MaxAge;
        List<Arrival> arrivals = (await db.MailMessages.AsNoTracking().IgnoreQueryFilters()
            .Where(m => ids.Contains(m.Id) && !m.IsRead && !m.IsDraft && !m.IsDeleted && m.ReceivedDate >= oldest && m.Folder!.Kind == FolderKind.Inbox)
            .Select(m => new Arrival(m.Id, m.MailboxId, m.FolderId, m.FromName, m.FromAddress, m.Subject, m.ReceivedDate))
            .ToListAsync(cancel)).OrderBy(a => a.ReceivedDate).ThenBy(a => a.Id).ToList();
        if (arrivals.Count == 0)
        {
            return [];
        }

        long[] mailboxIds = arrivals.Select(a => a.MailboxId).Distinct().ToArray();
        Dictionary<long, long?> owners = await db.Mailboxes.AsNoTracking().IgnoreQueryFilters()
            .Where(m => mailboxIds.Contains(m.Id) && m.IsActive)
            .ToDictionaryAsync(m => m.Id, m => m.OwnerUserId, cancel);
        var readers = (await db.MailboxPermissions.AsNoTracking().IgnoreQueryFilters()
            .Where(p => mailboxIds.Contains(p.MailboxId))
            .Select(p => new { p.MailboxId, p.UserId })
            .ToListAsync(cancel)).ToLookup(p => p.MailboxId, p => p.UserId);

        long[] userIds = owners.Values.Where(o => o is not null).Select(o => o!.Value).Concat(readers.SelectMany(g => g)).Distinct().ToArray();
        List<PushSubscription> subscriptions = await db.PushSubscriptions.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) && db.Users.IgnoreQueryFilters().Any(u => u.Id == s.UserId && u.IsActive))
            .ToListAsync(cancel);
        Dictionary<long, string?> cultures = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => subscriptions.Select(s => s.UserId).Distinct().Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Culture, cancel);

        var plans = new List<PlannedPush>();
        foreach (PushSubscription subscription in subscriptions)
        {
            // What this device is to be told about: the mailbox it owns, and, when it wants, those it may read.
            List<Arrival> mine = arrivals.Where(a =>
                owners.TryGetValue(a.MailboxId, out long? owner)
                && (owner == subscription.UserId || (!subscription.OwnMailboxOnly && readers[a.MailboxId].Contains(subscription.UserId)))).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            cultures.TryGetValue(subscription.UserId, out string? culture);
            plans.Add(Compose(subscription, mine, culture));
        }

        return plans;
    }

    private PlannedPush Compose(PushSubscription subscription, List<Arrival> arrivals, string? culture)
    {
        CultureInfo ui = Culture(culture);
        IStringLocalizer l = localizers.Create(typeof(SharedResource));
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = ui;
        try
        {
            Arrival latest = arrivals[^1];
            string who = Cut(string.IsNullOrWhiteSpace(latest.FromName) ? latest.FromAddress : latest.FromName, MaxTitle);
            string subject = string.IsNullOrWhiteSpace(latest.Subject) ? l["(no subject)"].Value : latest.Subject.Trim();
            bool several = arrivals.Count > 1;

            // One message: from whom, about what. Several: how many, and the newest one below.
            string title = several ? string.Format(ui, l["{0} new messages"].Value, arrivals.Count) : who;
            string body = several ? Cut(who + ": " + subject, MaxBody) : Cut(subject, MaxBody);
            string url = several
                ? $"/Mail#mailbox={latest.MailboxId}&folder={latest.FolderId}"
                : $"/Mail#mailbox={latest.MailboxId}&folder={latest.FolderId}&m={latest.Id}";

            byte[] message = JsonSerializer.SerializeToUtf8Bytes(new { title, body, url, tag = several ? "mail-" + latest.MailboxId : "mail-" + latest.Id });
            return new PlannedPush(subscription, message, "m" + latest.MailboxId);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>The language of a user (de-DE, en-US), else the one of the installation.</summary>
    private CultureInfo Culture(string? culture)
    {
        foreach (string? candidate in new[] { culture, config.Display.Culture })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    return CultureInfo.GetCultureInfo(candidate);
                }
                catch (CultureNotFoundException)
                {
                    // the next one
                }
            }
        }

        return CultureInfo.InvariantCulture;
    }

    private static string Cut(string text, int length)
    {
        string single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= length ? single : single[..(length - 1)] + "…";
    }
}

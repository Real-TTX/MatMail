using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>
/// A mailbox that has reached its storage limit: it takes no new mail until something is deleted. Thrown by <see cref="MailDelivery"/>
/// (nothing of that message is stored then, in no mailbox) and caught where the sender can be told: the SMTP server answers 452 (the
/// sender tries again later), a provider synchronisation leaves the mail at the provider.
/// </summary>
public sealed class MailboxFullException : Exception
{
    public MailboxFullException(IReadOnlyList<Mailbox> mailboxes)
        : base(mailboxes.Count == 1
            ? $"The mailbox \"{mailboxes[0].Name}\" is full: it takes no new mail until something is deleted."
            : $"The mailboxes {string.Join(", ", mailboxes.Select(m => $"\"{m.Name}\""))} are full: they take no new mail until something is deleted.")
    {
        MailboxIds = mailboxes.Select(m => m.Id).ToArray();
    }

    public IReadOnlyList<long> MailboxIds { get; }
}

/// <summary>
/// The storage limit of a mailbox (<see cref="Mailbox.QuotaBytes"/>, none by default). A mailbox is <b>full</b> when what it holds
/// (<see cref="MailboxUsageService"/>: the messages stored here, the stand-ins of live access do not count) has reached the limit; then
/// everything that would bring in new mail is refused, while deleting, moving inside the mailbox, and what the owner writes themselves
/// (drafts, copies in "Sent") still works, so that the owner can always make room. A single message may take the mailbox over its
/// limit: the rule looks at what is there, not at the size of what comes, which is not known when the sender is asked.
/// </summary>
public sealed class MailboxQuotaService
{
    private readonly MatMailDbContext _db;
    private readonly MailboxUsageService _usage;

    public MailboxQuotaService(MatMailDbContext db, MailboxUsageService usage)
    {
        _db = db;
        _usage = usage;
    }

    /// <summary>True when the mailbox has a limit and has reached it. Without a limit this costs nothing.</summary>
    public async Task<bool> IsFullAsync(Mailbox mailbox, CancellationToken cancel = default)
        => mailbox.QuotaBytes is long limit && (await _usage.GetAsync(mailbox.Id, cancel)).LocalBytes >= limit;

    /// <summary>The same for a mailbox known by its id (a mailbox of another tenant is looked up too: addresses are unique across the server).</summary>
    public async Task<bool> IsFullAsync(long mailboxId, CancellationToken cancel = default)
    {
        long? limit = await _db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.Id == mailboxId).Select(m => m.QuotaBytes).FirstOrDefaultAsync(cancel);
        return limit is long quota && (await _usage.GetAsync(mailboxId, cancel)).LocalBytes >= quota;
    }

    /// <summary>The mailboxes among the given ones that are full.</summary>
    public async Task<List<Mailbox>> FullAmongAsync(IEnumerable<Mailbox> mailboxes, CancellationToken cancel = default)
    {
        var full = new List<Mailbox>();
        foreach (Mailbox mailbox in mailboxes.DistinctBy(m => m.Id))
        {
            if (await IsFullAsync(mailbox, cancel))
            {
                full.Add(mailbox);
            }
        }

        return full;
    }
}

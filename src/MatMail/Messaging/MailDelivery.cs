using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>Where a message comes from; decides how recipients are found and what is recorded with the copy.</summary>
public sealed record DeliverySource
{
    /// <summary>The connected provider account that delivered the message (null: SMTP, web, internal).</summary>
    public MailAccount? Account { get; init; }
    public string? RemoteFolder { get; init; }
    public string? RemoteUid { get; init; }
    public DateTime? ReceivedDate { get; init; }
    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public bool IsAnswered { get; init; }
    public string[]? Keywords { get; init; }

    /// <summary>
    /// The recipients known from the SMTP envelope (RCPT TO). When empty the addresses are taken from the message headers
    /// (Delivered-To, X-Original-To, To, Cc) and, for plain provider accounts, the account's own address.
    /// </summary>
    public IReadOnlyList<string> EnvelopeRecipients { get; init; } = Array.Empty<string>();

    /// <summary>Limit address lookups to one tenant (provider accounts belong to a tenant).</summary>
    public long? TenantId { get; init; }
}

/// <summary>One mailbox that received (or already had) the message.</summary>
public sealed record DeliveredCopy(Mailbox Mailbox, MailMessage? Message, bool WasDuplicate, bool WentToUnassigned);

public sealed record DeliveryResult(IReadOnlyList<DeliveredCopy> Copies, IReadOnlyList<string> UnknownRecipients)
{
    public int Delivered => Copies.Count(c => c.Message is not null && !c.WasDuplicate);
}

/// <summary>
/// Routes incoming mail to mailboxes: by recipient address (exact), then the catch-all of its domain, then the domain's fallback
/// mailbox, and finally the tenant's "Unassigned" mailbox, so no downloaded message is ever lost.
/// </summary>
public sealed class MailDelivery
{
    private readonly MatMailDbContext _db;
    private readonly MailStore _store;
    private readonly FolderService _folders;
    private readonly MailboxService _mailboxes;

    public MailDelivery(MatMailDbContext db, MailStore store, FolderService folders, MailboxService mailboxes)
    {
        _db = db;
        _store = store;
        _folders = folders;
        _mailboxes = mailboxes;
    }

    /// <summary>
    /// Delivers a raw message to every mailbox its recipients lead to (one copy per mailbox, in the Inbox). Mail that nobody claims
    /// goes to the tenant's "Unassigned" mailbox. Throws when no tenant can be determined for a message that matches nothing.
    /// </summary>
    public async Task<DeliveryResult> DeliverAsync(byte[] raw, DeliverySource source, CancellationToken cancel = default)
    {
        ParsedMessage parsed = MessageParser.Parse(raw);
        List<string> candidates = CollectRecipients(parsed, source);

        var targets = new Dictionary<long, (Mailbox Mailbox, List<string> Addresses, bool Unassigned)>();
        var unknown = new List<string>();

        foreach (string address in candidates)
        {
            Resolution? resolution = await ResolveAsync(address, source.TenantId, cancel);
            if (resolution is null)
            {
                unknown.Add(address);
                continue;
            }

            if (!targets.TryGetValue(resolution.Mailbox.Id, out var entry))
            {
                entry = (resolution.Mailbox, new List<string>(), resolution.Unassigned);
                targets[resolution.Mailbox.Id] = entry;
            }

            entry.Addresses.Add(address);
        }

        if (targets.Count == 0)
        {
            // Nobody claims the message: it must not vanish. The tenant of the source (or of the first known domain) keeps it.
            long? tenantId = source.TenantId ?? source.Account?.TenantId;
            if (tenantId is null)
            {
                return new DeliveryResult(Array.Empty<DeliveredCopy>(), unknown);
            }

            Mailbox unassigned = await _mailboxes.GetUnassignedMailboxAsync(tenantId.Value);
            targets[unassigned.Id] = (unassigned, candidates, true);
        }

        var copies = new List<DeliveredCopy>();
        foreach ((Mailbox mailbox, List<string> addresses, bool wentToUnassigned) in targets.Values)
        {
            copies.Add(await StoreAsync(mailbox, raw, parsed, source, addresses, wentToUnassigned, cancel));
        }

        return new DeliveryResult(copies, unknown);
    }

    /// <summary>Stores a message in a given mailbox' Inbox without any routing (migration/backup targets, internal copies).</summary>
    public async Task<DeliveredCopy> DeliverToMailboxAsync(Mailbox mailbox, byte[] raw, DeliverySource source, FolderKind folderKind = FolderKind.Inbox, CancellationToken cancel = default)
    {
        ParsedMessage parsed = MessageParser.Parse(raw);
        return await StoreAsync(mailbox, raw, parsed, source, source.EnvelopeRecipients.ToList(), false, cancel, folderKind);
    }

    /// <summary>
    /// Which mailbox an address leads to. Null when the domain is not registered (or belongs to another tenant than
    /// <paramref name="onlyTenant"/>): the address is not local.
    /// </summary>
    public async Task<Resolution?> ResolveAsync(string address, long? onlyTenant = null, CancellationToken cancel = default)
    {
        string normalized = MailAddresses.Normalize(address);
        if (!MailAddresses.TrySplit(normalized, out _, out string domain))
        {
            return null;
        }

        MailboxAlias? alias = await _db.MailboxAliases.IgnoreQueryFilters().AsNoTracking().Include(a => a.Mailbox)
            .FirstOrDefaultAsync(a => a.Address == normalized, cancel);
        if (alias?.Mailbox is { IsActive: true } && (onlyTenant is null || alias.TenantId == onlyTenant))
        {
            return new Resolution(alias.Mailbox, false, alias.TenantId);
        }

        Domain? domainRow = await _db.Domains.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.Name == domain && d.IsActive, cancel);
        if (domainRow is null || (onlyTenant is not null && domainRow.TenantId != onlyTenant))
        {
            return null;
        }

        MailboxAlias? catchAll = await _db.MailboxAliases.IgnoreQueryFilters().AsNoTracking().Include(a => a.Mailbox)
            .FirstOrDefaultAsync(a => a.Address == MailAddresses.CatchAllOf(domain), cancel);
        if (catchAll?.Mailbox is { IsActive: true })
        {
            return new Resolution(catchAll.Mailbox, false, domainRow.TenantId);
        }

        if (domainRow.CatchAllMailboxId is long fallbackId)
        {
            Mailbox? fallback = await _db.Mailboxes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(m => m.Id == fallbackId && m.IsActive, cancel);
            if (fallback is not null)
            {
                return new Resolution(fallback, false, domainRow.TenantId);
            }
        }

        Mailbox unassigned = await _mailboxes.GetUnassignedMailboxAsync(domainRow.TenantId);
        return new Resolution(unassigned, true, domainRow.TenantId);
    }

    /// <summary>True when the address belongs to a registered domain (so the server is responsible for it).</summary>
    public Task<bool> IsLocalDomainAsync(string domain, CancellationToken cancel = default)
        => _db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Name == domain.ToLowerInvariant() && d.IsActive, cancel);

    // ---------------------------------------------------------------------------------------------------------------

    private async Task<DeliveredCopy> StoreAsync(
        Mailbox mailbox, byte[] raw, ParsedMessage parsed, DeliverySource source, List<string> addresses, bool wentToUnassigned,
        CancellationToken cancel, FolderKind folderKind = FolderKind.Inbox)
    {
        await _mailboxes.EnsureDefaultFoldersAsync(mailbox);
        MailFolder folder = await _folders.FindByKindAsync(mailbox.Id, folderKind, cancel)
            ?? throw new InvalidOperationException($"Mailbox {mailbox.Id} has no {folderKind} folder.");

        // The same message (same Message-ID) is not stored twice in one mailbox' folder, e.g. when it was addressed to two aliases
        // of the mailbox or fetched from two provider accounts.
        if (parsed.MessageId is not null && source.Account?.Role != MailAccountRole.Backup && source.Account?.Role != MailAccountRole.Migration)
        {
            MailMessage? existing = await _db.MailMessages.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(m => m.MailboxId == mailbox.Id && m.FolderId == folder.Id && m.MessageIdHeader == parsed.MessageId, cancel);
            if (existing is not null)
            {
                return new DeliveredCopy(mailbox, existing, true, wentToUnassigned);
            }
        }

        MailMessage message = await _store.AddAsync(folder.Id, new NewMessage(raw)
        {
            ReceivedDate = source.ReceivedDate,
            IsRead = source.IsRead,
            IsStarred = source.IsStarred,
            IsAnswered = source.IsAnswered,
            Keywords = source.Keywords,
            SourceAccountId = source.Account?.Id,
            RemoteFolder = source.RemoteFolder,
            RemoteUid = source.RemoteUid,
            EnvelopeRecipients = addresses.Count == 0 ? null : string.Join(", ", addresses.Distinct()),
        }, cancel);

        return new DeliveredCopy(mailbox, message, false, wentToUnassigned);
    }

    /// <summary>
    /// The addresses to route for. Envelope recipients win; otherwise the headers a provider or MTA leaves (Delivered-To, ...),
    /// then To/Cc, and for a plain (non catch-all) provider account its own address.
    /// </summary>
    private static List<string> CollectRecipients(ParsedMessage parsed, DeliverySource source)
    {
        var result = new List<string>();
        if (source.EnvelopeRecipients.Count > 0)
        {
            result.AddRange(source.EnvelopeRecipients.Select(MailAddresses.Normalize));
        }
        else
        {
            result.AddRange(parsed.DeliveredTo);
            bool plainAccount = source.Account is { IsCatchAll: false } a && !string.IsNullOrWhiteSpace(a.Address);
            if (result.Count == 0 && plainAccount)
            {
                // A provider mailbox holds mail for its own address (the message may name others in To/Cc, e.g. mailing lists).
                result.Add(MailAddresses.Normalize(source.Account!.Address));
            }

            if (result.Count == 0)
            {
                result.AddRange(parsed.Recipients);
            }
        }

        return result.Where(MailAddresses.IsValid).Distinct().ToList();
    }

    /// <summary>The outcome of resolving an address: the target mailbox, whether it is the "Unassigned" fallback, the tenant.</summary>
    public sealed record Resolution(Mailbox Mailbox, bool Unassigned, long TenantId);
}

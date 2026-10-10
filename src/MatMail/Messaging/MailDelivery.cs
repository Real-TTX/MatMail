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

    /// <summary>Remote: the raw bytes are only a header stub, the message stays at the provider (live access).</summary>
    public MessageStorage Storage { get; init; } = MessageStorage.Local;

    /// <summary>The door the message came through. When set, the delivery is written to the mail transfer log.</summary>
    public TransferChannel? Channel { get; init; }

    /// <summary>Who or what was on the other end (the connected account, the user, the rule), for the transfer log.</summary>
    public string? Peer { get; init; }

    /// <summary>The address of the sending server or client, for the transfer log.</summary>
    public string? RemoteIp { get; init; }

    /// <summary>The envelope sender (MAIL FROM), for the transfer log; without it the From: header is shown.</summary>
    public string? EnvelopeSender { get; init; }
}

/// <summary>
/// One mailbox that received (or already had) the message. <see cref="Message"/> is null when a rule of the mailbox deleted it;
/// <see cref="Rules"/> says what the rules of the mailbox decided.
/// </summary>
public sealed record DeliveredCopy(Mailbox Mailbox, MailMessage? Message, bool WasDuplicate, bool WentToUnassigned, RuleOutcome? Rules = null);

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
    private readonly MailRuleEngine _rules;
    private readonly TransferLog _transfers;
    private readonly ActivityLogger _log;
    private readonly MailboxQuotaService _quota;

    public MailDelivery(MatMailDbContext db, MailStore store, FolderService folders, MailboxService mailboxes, MailRuleEngine rules, TransferLog transfers, ActivityLogger log, MailboxQuotaService quota)
    {
        _db = db;
        _store = store;
        _folders = folders;
        _mailboxes = mailboxes;
        _rules = rules;
        _transfers = transfers;
        _log = log;
        _quota = quota;
    }

    /// <summary>
    /// Delivers a raw message to every mailbox its recipients lead to (one copy per mailbox, in the Inbox). Mail that nobody claims
    /// goes to the tenant's "Unassigned" mailbox. Throws when no tenant can be determined for a message that matches nothing, and a
    /// <see cref="MailboxFullException"/> (nothing stored) when one of the mailboxes has reached its storage limit.
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
                if (source.Channel is TransferChannel unclaimedChannel)
                {
                    await RecordTransferAsync(unclaimedChannel, raw, parsed, source, candidates, new List<DeliveredCopy>(), unknown);
                }

                return new DeliveryResult(Array.Empty<DeliveredCopy>(), unknown);
            }

            Mailbox unassigned = await _mailboxes.GetUnassignedMailboxAsync(tenantId.Value);
            targets[unassigned.Id] = (unassigned, candidates, true);
        }

        // A full mailbox takes no new mail. All or nothing: if any of the mailboxes is full, none gets the message, so that the sender (who is
        // told to try again) or the provider (which keeps the mail) does not deliver it twice to the others. The stand-in of a message that
        // stays at the provider (live access) takes no room here and is never refused.
        if (source.Storage == MessageStorage.Local)
        {
            List<Mailbox> full = await _quota.FullAmongAsync(targets.Values.Select(t => t.Mailbox), cancel);
            if (full.Count > 0)
            {
                throw new MailboxFullException(full);
            }
        }

        var copies = new List<DeliveredCopy>();
        foreach ((Mailbox mailbox, List<string> addresses, bool wentToUnassigned) in targets.Values)
        {
            copies.Add(await StoreAsync(mailbox, raw, parsed, source, addresses, wentToUnassigned, cancel));
        }

        if (source.Channel is TransferChannel channel)
        {
            await RecordTransferAsync(channel, raw, parsed, source, candidates, copies, unknown);
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
        MailFolder inbox = await _folders.FindByKindAsync(mailbox.Id, folderKind, cancel)
            ?? throw new InvalidOperationException($"Mailbox {mailbox.Id} has no {folderKind} folder.");

        // The rules of the mailbox decide before the message is stored: it arrives where it belongs, already read, starred or labelled.
        RuleOutcome rules = UsesRules(source, folderKind, wentToUnassigned) ? await EvaluateRulesAsync(mailbox, raw, parsed, source, addresses, cancel) : RuleOutcome.None;
        MailFolder folder = rules.TargetFolderId is long targetId
            ? await _db.MailFolders.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(f => f.Id == targetId && f.MailboxId == mailbox.Id, cancel) ?? inbox
            : inbox;

        // The same message (same Message-ID) is not stored twice in one mailbox' folder, e.g. when it was addressed to two aliases
        // of the mailbox or fetched from two provider accounts.
        if (parsed.MessageId is not null && source.Account?.Role != MailAccountRole.Backup && source.Account?.Role != MailAccountRole.Migration)
        {
            long[] folderIds = { inbox.Id, folder.Id };
            MailMessage? existing = await _db.MailMessages.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(m => m.MailboxId == mailbox.Id && folderIds.Contains(m.FolderId) && m.MessageIdHeader == parsed.MessageId, cancel);
            if (existing is not null)
            {
                return new DeliveredCopy(mailbox, existing, true, wentToUnassigned);
            }
        }

        if (rules.Discard)
        {
            await ForwardAsync(mailbox, raw, parsed, source, rules, cancel);
            return new DeliveredCopy(mailbox, null, false, wentToUnassigned, rules);
        }

        MailMessage message = await _store.AddAsync(folder.Id, new NewMessage(raw)
        {
            ReceivedDate = source.ReceivedDate,
            IsRead = source.IsRead || rules.MarkRead,
            IsStarred = source.IsStarred || rules.Star,
            IsAnswered = source.IsAnswered,
            Keywords = rules.Labels.Count == 0 ? source.Keywords : (source.Keywords ?? Array.Empty<string>()).Union(rules.Labels, StringComparer.OrdinalIgnoreCase).ToArray(),
            SourceAccountId = source.Account?.Id,
            RemoteFolder = source.RemoteFolder,
            RemoteUid = source.RemoteUid,
            EnvelopeRecipients = addresses.Count == 0 ? null : string.Join(", ", addresses.Distinct()),
            Storage = source.Storage,
        }, cancel);

        // Passed on only once it is stored here: a message that comes back round through a loop of forwarding rules then finds its copy
        // and is not stored and forwarded again.
        await ForwardAsync(mailbox, raw, parsed, source, rules, cancel);
        return new DeliveredCopy(mailbox, message, false, wentToUnassigned, rules);
    }

    /// <summary>Mail that arrives in an inbox through the routing is run through the rules of the mailbox; copies for backups, migrations and the "Unassigned" bucket are not.</summary>
    private static bool UsesRules(DeliverySource source, FolderKind folderKind, bool wentToUnassigned)
        => folderKind == FolderKind.Inbox && !wentToUnassigned && source.Account?.Role is null or MailAccountRole.Mail;

    /// <summary>A problem of a rule never costs the message: it is stored as if the mailbox had no rules.</summary>
    private async Task<RuleOutcome> EvaluateRulesAsync(Mailbox mailbox, byte[] raw, ParsedMessage parsed, DeliverySource source, List<string> addresses, CancellationToken cancel)
    {
        try
        {
            return await _rules.EvaluateAsync(mailbox, raw, parsed, addresses, source.Storage == MessageStorage.Remote, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.ErrorAsync(ActivityCategory.System, $"The rules of mailbox {mailbox.Name} could not be applied; the message was stored without them.", ex.Message, mailbox.TenantId);
            return RuleOutcome.None;
        }
    }

    /// <summary>Forwarding is done for the messages the rules say so about; the stand-ins of live access have no text to forward.</summary>
    private async Task ForwardAsync(Mailbox mailbox, byte[] raw, ParsedMessage parsed, DeliverySource source, RuleOutcome rules, CancellationToken cancel)
    {
        if (rules.Forwards.Count == 0 || source.Storage == MessageStorage.Remote)
        {
            return;
        }

        try
        {
            await _rules.ForwardAsync(mailbox, raw, parsed.Subject, rules.Forwards, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.ErrorAsync(ActivityCategory.System, $"A rule of mailbox {mailbox.Name} could not forward a message.", ex.Message, mailbox.TenantId);
        }
    }

    /// <summary>One line in the mail transfer log for a delivery: who sent it, who got it, and what the rules did.</summary>
    private async Task RecordTransferAsync(
        TransferChannel channel, byte[] raw, ParsedMessage parsed, DeliverySource source, List<string> addressed, List<DeliveredCopy> copies, List<string> unknown)
    {
        var where = new List<string>();
        foreach (DeliveredCopy copy in copies)
        {
            string name = copy.Mailbox.Name;
            if (copy.Message is null && copy.Rules?.Discard == true)
            {
                where.Add($"{name} (deleted by a rule)");
            }
            else if (copy.WasDuplicate)
            {
                where.Add($"{name} (already there)");
            }
            else if (copy.WentToUnassigned)
            {
                where.Add($"{name} (unassigned)");
            }
            else if (copy.Rules is { Matched: true })
            {
                where.Add($"{name} ({copy.Rules.MatchedRuleIds.Count} rule(s) applied)");
            }
            else
            {
                where.Add(name);
            }
        }

        string detail = copies.Count == 0 ? "Not delivered: no mailbox." : "Delivered to " + string.Join(", ", where) + ".";
        if (unknown.Count > 0)
        {
            detail += " Unknown recipients: " + string.Join(", ", unknown) + ".";
        }

        bool inbound = channel is TransferChannel.SmtpServer or TransferChannel.ProviderAccount;
        bool allDiscarded = copies.Count > 0 && copies.All(c => c.Message is null && c.Rules?.Discard == true);
        await _transfers.RecordAsync(
            inbound ? TransferDirection.Inbound : TransferDirection.Internal,
            channel,
            copies.Count == 0 ? TransferStatus.Failed : allDiscarded ? TransferStatus.Discarded : TransferStatus.Delivered,
            string.IsNullOrWhiteSpace(source.EnvelopeSender) ? parsed.FromAddress : source.EnvelopeSender,
            addressed,
            parsed.Subject,
            source.Storage == MessageStorage.Remote ? 0 : raw.LongLength,
            copies.FirstOrDefault()?.Mailbox.TenantId ?? source.TenantId ?? source.Account?.TenantId,
            parsed.MessageId,
            source.Peer ?? source.Account?.Name,
            source.RemoteIp,
            detail);
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

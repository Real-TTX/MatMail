using System.Globalization;
using System.Text;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>What the rules of a mailbox decided for one incoming message.</summary>
/// <param name="TargetFolderId">The folder the message goes into instead of the inbox (null: the inbox).</param>
/// <param name="MarkRead">The message is stored as read.</param>
/// <param name="Star">The message is stored with a star.</param>
/// <param name="Labels">Labels (IMAP keywords) the message gets.</param>
/// <param name="Discard">The message is not stored at all.</param>
/// <param name="Forwards">Addresses a copy is sent to.</param>
/// <param name="MatchedRuleIds">The rules that matched, in the order they were applied.</param>
public sealed record RuleOutcome(
    long? TargetFolderId, bool MarkRead, bool Star, IReadOnlyList<string> Labels, bool Discard, IReadOnlyList<string> Forwards, IReadOnlyList<long> MatchedRuleIds)
{
    public static readonly RuleOutcome None = new(null, false, false, Array.Empty<string>(), false, Array.Empty<string>(), Array.Empty<long>());

    public bool Matched => MatchedRuleIds.Count > 0;

    /// <summary>The rules changed how the message is stored (state, labels or place).</summary>
    public bool ChangesMessage => MarkRead || Star || Labels.Count > 0 || TargetFolderId is not null;
}

/// <summary>
/// The rules of a mailbox: decides for an incoming message what happens to it (see <see cref="MailRule"/>). The decision is made before the
/// message is stored, so it arrives where it belongs — read, starred, labelled or in its folder — and nothing flickers through the inbox or
/// triggers a notification. A message is never lost to a problem of a rule: whoever stores it carries on without rules when this throws.
/// </summary>
public sealed class MailRuleEngine
{
    /// <summary>A message forwarded more often than this is going round in circles between mailboxes with forwarding rules.</summary>
    public const int MaxForwardHops = 5;

    private const string HopsHeader = "X-MatMail-Forward-Hops";
    private const int MaxForwardsPerMessage = 10;

    private readonly MatMailDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly AppConfig _config;
    private readonly ActivityLogger _log;

    public MailRuleEngine(MatMailDbContext db, IServiceScopeFactory scopes, AppConfig config, ActivityLogger log)
    {
        _db = db;
        _scopes = scopes;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Runs the active rules of the mailbox over the message, in their order, until one that stops processing has matched.
    /// <paramref name="onlyHeaders"/>: the message is a stand-in of live access (only the header is here), so conditions on the text,
    /// the size and the attachments cannot be judged and do not match.
    /// </summary>
    public async Task<RuleOutcome> EvaluateAsync(
        Mailbox mailbox, byte[] raw, ParsedMessage parsed, IReadOnlyCollection<string> envelopeRecipients, bool onlyHeaders, CancellationToken cancel = default)
    {
        List<MailRule> rules = await _db.MailRules.IgnoreQueryFilters().AsNoTracking()
            .Include(r => r.Conditions).Include(r => r.Actions)
            .Where(r => r.MailboxId == mailbox.Id && r.IsActive)
            .OrderBy(r => r.Position).ThenBy(r => r.Id)
            .ToListAsync(cancel);
        if (rules.Count == 0)
        {
            return RuleOutcome.None;
        }

        var facts = new MessageFacts(raw, parsed, envelopeRecipients, onlyHeaders);
        Dictionary<long, FolderKind> folders = await _db.MailFolders.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.MailboxId == mailbox.Id)
            .ToDictionaryAsync(f => f.Id, f => f.Kind, cancel);

        long? target = null;
        bool read = false, star = false, discard = false;
        var labels = new List<string>();
        var forwards = new List<string>();
        var matched = new List<long>();

        foreach (MailRule rule in rules)
        {
            if (!Matches(rule, facts))
            {
                continue;
            }

            matched.Add(rule.Id);
            foreach (MailRuleAction action in rule.Actions)
            {
                switch (action.Type)
                {
                    case RuleActionType.MoveToFolder when action.FolderId is long folderId && folders.ContainsKey(folderId):
                        target = folderId;
                        break;
                    case RuleActionType.MoveToTrash:
                        long? trash = folders.Where(f => f.Value == FolderKind.Trash).Select(f => (long?)f.Key).FirstOrDefault();
                        target = trash ?? target;
                        break;
                    case RuleActionType.MarkAsRead:
                        read = true;
                        break;
                    case RuleActionType.Star:
                        star = true;
                        break;
                    case RuleActionType.AddLabel when ToKeyword(action.Value) is { } label:
                        labels.Add(label);
                        break;
                    case RuleActionType.Discard:
                        discard = true;
                        break;
                    case RuleActionType.ForwardTo when MailAddresses.IsValid(action.Value):
                        forwards.Add(MailAddresses.Normalize(action.Value));
                        break;
                }
            }

            if (discard || rule.StopProcessing)
            {
                break;
            }
        }

        if (matched.Count == 0)
        {
            return RuleOutcome.None;
        }

        DateTime now = DateTime.UtcNow;
        await _db.MailRules.IgnoreQueryFilters().Where(r => matched.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MatchCount, r => r.MatchCount + 1).SetProperty(r => r.LastMatchDate, (DateTime?)now), cancel);
        return new RuleOutcome(
            target, read, star, labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), discard,
            forwards.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxForwardsPerMessage).ToList(), matched);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Conditions
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Whether the rule applies: all (or any) of its conditions hold. A rule without conditions never applies.</summary>
    public static bool Matches(MailRule rule, MessageFacts facts)
        => rule.Conditions.Count > 0
           && (rule.Match == RuleMatch.Any ? rule.Conditions.Any(c => Holds(c, facts)) : rule.Conditions.All(c => Holds(c, facts)));

    private static bool Holds(MailRuleCondition condition, MessageFacts facts)
    {
        string value = condition.Value.Trim();
        switch (condition.Field)
        {
            case RuleField.HasAttachment:
                return !facts.OnlyHeaders && facts.Parsed.HasAttachments == value.Equals("true", StringComparison.OrdinalIgnoreCase);
            case RuleField.Size:
                if (facts.OnlyHeaders || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double kilobytes))
                {
                    return false;
                }

                double size = facts.Raw.LongLength / 1024.0;
                return condition.Operator switch
                {
                    RuleOperator.LargerThan => size > kilobytes,
                    RuleOperator.SmallerThan => size < kilobytes,
                    _ => false,
                };
        }

        if (value.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> candidates = condition.Field switch
        {
            RuleField.From => facts.From,
            RuleField.To => facts.To,
            RuleField.Subject => new[] { facts.Parsed.Subject },
            RuleField.Body => facts.OnlyHeaders ? Array.Empty<string>() : new[] { facts.Parsed.SearchText },
            RuleField.Header => facts.HeaderValues(condition.HeaderName),
            _ => Array.Empty<string>(),
        };

        // What cannot be looked at (the text of a live-access stand-in, a header that is not there) satisfies no condition, not even a negative one.
        if (candidates.Count == 0 || (condition.Field == RuleField.Body && facts.OnlyHeaders))
        {
            return false;
        }

        return condition.Operator switch
        {
            RuleOperator.Contains => candidates.Any(c => c.Contains(value, StringComparison.OrdinalIgnoreCase)),
            RuleOperator.DoesNotContain => !candidates.Any(c => c.Contains(value, StringComparison.OrdinalIgnoreCase)),
            RuleOperator.Equals => candidates.Any(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)),
            RuleOperator.StartsWith => candidates.Any(c => c.StartsWith(value, StringComparison.OrdinalIgnoreCase)),
            RuleOperator.EndsWith => candidates.Any(c => c.EndsWith(value, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>What a rule can look at in a message; the pieces are worked out when a condition asks for them.</summary>
    public sealed class MessageFacts
    {
        private readonly Lazy<HeaderList> _headers;

        public MessageFacts(byte[] raw, ParsedMessage parsed, IReadOnlyCollection<string> envelopeRecipients, bool onlyHeaders)
        {
            Raw = raw;
            Parsed = parsed;
            OnlyHeaders = onlyHeaders;
            From = new[]
            {
                string.IsNullOrEmpty(parsed.FromName) ? parsed.FromAddress : $"{parsed.FromName} <{parsed.FromAddress}>",
                parsed.FromAddress,
                parsed.FromName,
            }.Where(s => s.Length > 0).Distinct().ToList();
            To = new[] { parsed.ToSummary }.Concat(parsed.Recipients).Concat(envelopeRecipients).Where(s => s.Length > 0).Distinct().ToList();
            _headers = new Lazy<HeaderList>(() =>
            {
                using var stream = new MemoryStream(parsed.HeaderBytes, writable: false);
                return HeaderList.Load(ParserOptions.Default, stream);
            });
        }

        public byte[] Raw { get; }
        public ParsedMessage Parsed { get; }
        public bool OnlyHeaders { get; }
        public IReadOnlyList<string> From { get; }
        public IReadOnlyList<string> To { get; }

        public IReadOnlyList<string> HeaderValues(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Array.Empty<string>();
            }

            string wanted = name.Trim().TrimEnd(':');
            return _headers.Value.Where(h => h.Field.Equals(wanted, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        }
    }

    /// <summary>A label as an IMAP keyword: letters, digits and a few signs, no blanks (they become underscores).</summary>
    public static string? ToKeyword(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var keyword = new StringBuilder();
        foreach (char c in label.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '$' or '-' or '_' or '.')
            {
                keyword.Append(c);
            }
            else if (char.IsWhiteSpace(c) && keyword.Length > 0 && keyword[^1] != '_')
            {
                keyword.Append('_');
            }
        }

        string result = keyword.ToString().Trim('_');
        return result.Length == 0 ? null : result.Length > 40 ? result[..40] : result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Forwarding
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a copy of the message to another address, the way a mail program "redirects" it: the message itself stays as it is (so the
    /// signature of the sender stays valid), <c>Resent-*</c> headers say who passed it on, and the envelope sender is an address of the
    /// mailbox, so the provider that carries it accepts it. Addresses on this server get it delivered at once, all others through the queue.
    /// </summary>
    public async Task ForwardAsync(Mailbox mailbox, byte[] raw, string? subject, IEnumerable<string> targets, CancellationToken cancel = default)
    {
        int hops = HopsOf(raw);
        if (hops >= MaxForwardHops)
        {
            await _log.WarnAsync(ActivityCategory.System, $"A message was not forwarded again by the rules of {mailbox.Name}: it has been forwarded {hops} times already (a loop?).", tenantId: mailbox.TenantId);
            return;
        }

        List<MailboxAlias> aliases = await _db.MailboxAliases.IgnoreQueryFilters().AsNoTracking().Where(a => a.MailboxId == mailbox.Id).ToListAsync(cancel);
        MailboxAlias? sender = aliases.Where(a => a.CanSend && !a.IsCatchAll).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address).FirstOrDefault();
        if (sender is null)
        {
            await _log.WarnAsync(ActivityCategory.System, $"A rule of {mailbox.Name} could not forward a message: the mailbox has no address it may send as.", tenantId: mailbox.TenantId);
            return;
        }

        foreach (string target in targets.Select(MailAddresses.Normalize).Distinct())
        {
            if (aliases.Any(a => a.Address == target))
            {
                continue;
            }

            byte[] resent = AddResentHeaders(raw, sender.Address, target, hops + 1, _config.Server.Hostname);
            try
            {
                await SendCopyAsync(mailbox, sender.Address, target, resent, subject, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _log.ErrorAsync(ActivityCategory.System, $"A rule of {mailbox.Name} could not forward a message to {target}.", ex.Message, mailbox.TenantId);
            }
        }
    }

    private async Task SendCopyAsync(Mailbox mailbox, string envelopeFrom, string target, byte[] resent, string? subject, CancellationToken cancel)
    {
        // Delivery runs as the system in a scope of its own: the target may live in another tenant than the mailbox.
        using IServiceScope scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var delivery = scope.ServiceProvider.GetRequiredService<MailDelivery>();
        string origin = $"rule of {mailbox.Name}";

        if (await delivery.ResolveAsync(target, null, cancel) is not null)
        {
            await delivery.DeliverAsync(resent, new DeliverySource
            {
                EnvelopeRecipients = new[] { target },
                Channel = TransferChannel.Rule,
                Peer = origin,
                EnvelopeSender = envelopeFrom,
            }, cancel);
            return;
        }

        var routing = scope.ServiceProvider.GetRequiredService<SendRouting>();
        MailAccount? account = await routing.ResolveAccountAsync(mailbox.TenantId, envelopeFrom, null, cancel);
        if (account is null && !_config.Queue.AllowDirectDelivery)
        {
            await _log.WarnAsync(ActivityCategory.Queue, $"A rule of {mailbox.Name} could not forward a message to {target}: no sending account fits and direct delivery is switched off.", tenantId: mailbox.TenantId);
            return;
        }

        var queue = scope.ServiceProvider.GetRequiredService<OutboundQueue>();
        await queue.EnqueueAsync(
            mailbox.TenantId, account?.Id, envelopeFrom, new[] { target }, resent, subject ?? string.Empty, mailbox.Id, null,
            new TransferOrigin(TransferChannel.Rule, origin, null), cancel);
    }

    /// <summary>How often the message went through a forwarding rule already.</summary>
    public static int HopsOf(byte[] raw)
        => int.TryParse(RawHeaders.Get(raw, HopsHeader), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hops) ? Math.Max(0, hops) : 0;

    /// <summary>Puts the headers of a redirected message in front of it; the message itself is not touched.</summary>
    public static byte[] AddResentHeaders(byte[] raw, string from, string to, int hops, string hostname)
    {
        string host = string.IsNullOrWhiteSpace(hostname) ? "localhost" : hostname;
        string text =
            $"Resent-Date: {DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\r\n" +
            $"Resent-From: <{from}>\r\n" +
            $"Resent-To: <{to}>\r\n" +
            $"Resent-Message-ID: <{Guid.NewGuid():N}@{host}>\r\n" +
            $"{HopsHeader}: {hops}\r\n";
        byte[] prefix = Encoding.ASCII.GetBytes(text);
        byte[] result = new byte[prefix.Length + raw.Length];
        prefix.CopyTo(result, 0);
        raw.CopyTo(result, prefix.Length);
        return result;
    }
}

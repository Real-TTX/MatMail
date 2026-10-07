using System.Net;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MimeKit;

namespace MatMail.MailServer.Smtp;

/// <summary>Who is sending in an SMTP transaction; decides what the client may do.</summary>
internal enum SmtpClientKind
{
    /// <summary>Signed in (AUTH): sends as their own and delegated addresses, to anybody.</summary>
    Authenticated,

    /// <summary>In the network of a relay rule, with a sender the rule allows: sends to anybody (smart host for printers, servers).</summary>
    Trusted,

    /// <summary>Anybody else, e.g. other mail servers: may only deliver to local addresses.</summary>
    Anonymous,
}

/// <summary>One mail transaction (MAIL FROM … DATA).</summary>
internal sealed class SmtpTransaction
{
    public SmtpTransaction(SmtpClientKind kind, string sender)
    {
        Kind = kind;
        Sender = sender;
    }

    public SmtpClientKind Kind { get; }

    /// <summary>The normalised envelope sender; empty for the null sender "&lt;&gt;".</summary>
    public string Sender { get; }

    /// <summary>The tenant that sends (signed-in user's or relay rule's); null for anonymous mail.</summary>
    public long? TenantId { get; init; }
    public MailUser? User { get; init; }
    public SendIdentity? Identity { get; init; }
    public RelayRule? Rule { get; init; }
    public List<string> Recipients { get; } = new();

    /// <summary>Whether external recipients can be routed at all (asked once per transaction).</summary>
    public bool? CanRouteExternal { get; set; }
}

/// <summary>
/// The relay policy of the SMTP server — the part that makes relay abuse impossible. Signed-in users send as the addresses they
/// own or hold "send" rights for (envelope and From: header); clients of a relay rule send as the senders the rule allows;
/// everybody else may only deliver to local addresses and may not claim one of the local domains as sender.
/// </summary>
internal sealed class SmtpPolicy
{
    private readonly MailAccessService _access;
    private readonly RelayPolicy _relay;
    private readonly MailDelivery _delivery;
    private readonly SendRouting _routing;
    private readonly AppConfig _config;

    public SmtpPolicy(IServiceProvider services)
    {
        _access = services.GetRequiredService<MailAccessService>();
        _relay = services.GetRequiredService<RelayPolicy>();
        _delivery = services.GetRequiredService<MailDelivery>();
        _routing = services.GetRequiredService<SendRouting>();
        _config = services.GetRequiredService<AppConfig>();
    }

    public Task<RelayRule?> FindRuleAsync(IPAddress remote, CancellationToken cancel) => _relay.FindRuleAsync(remote, cancel);

    /// <summary>
    /// "&lt;Postmaster&gt;" without a domain (RFC 5321 4.5.1) is the postmaster of this server: at the first registered domain among the
    /// host name and its parents (mail.example.com → example.com), else at the host name itself.
    /// </summary>
    public async Task<string> PostmasterAddressAsync(string hostname, CancellationToken cancel)
    {
        string host = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        for (string candidate = host; candidate.Contains('.'); candidate = candidate[(candidate.IndexOf('.') + 1)..])
        {
            if (await _delivery.IsLocalDomainAsync(candidate, cancel))
            {
                return "postmaster@" + candidate;
            }
        }

        return "postmaster@" + host;
    }

    /// <summary>
    /// MAIL FROM: decides who the client is for this transaction. A client of a relay rule whose sender the rule does not allow is
    /// treated like any other client (local recipients only). On the submission ports anonymous clients are refused (RFC 6409).
    /// Returns the transaction, or the reply that refuses the sender.
    /// </summary>
    public async Task<(SmtpTransaction? Transaction, string? Rejection)> CheckSenderAsync(
        string sender, MailUser? user, RelayRule? rule, bool submissionPort, CancellationToken cancel)
    {
        if (user is not null)
        {
            SendIdentity? identity = sender.Length == 0 ? null : await _access.FindSendIdentityAsync(user, sender, cancel);
            return identity is null
                ? (null, $"553 5.7.1 <{sender}>: Sender address rejected: not owned by the signed-in user")
                : (new SmtpTransaction(SmtpClientKind.Authenticated, sender) { TenantId = user.TenantId, User = user, Identity = identity }, null);
        }

        if (rule is not null && await _relay.IsSenderAllowedAsync(rule, sender, cancel))
        {
            return (new SmtpTransaction(SmtpClientKind.Trusted, sender) { TenantId = rule.TenantId, Rule = rule }, null);
        }

        if (submissionPort)
        {
            return (null, "530 5.7.0 Authentication required");
        }

        string domain = MailAddresses.DomainOf(sender);
        if (domain.Length > 0 && await _delivery.IsLocalDomainAsync(domain, cancel))
        {
            return (null, $"550 5.7.1 <{sender}>: Sender address rejected: {domain} is a local domain, please sign in");
        }

        return (new SmtpTransaction(SmtpClientKind.Anonymous, sender), null);
    }

    /// <summary>
    /// RCPT TO: null when the recipient is accepted, else the refusing reply. Addresses of registered domains are always accepted —
    /// one without a mailbox ends in the tenant's "Unassigned" mailbox on purpose. Anything else needs a signed-in or trusted client.
    /// </summary>
    public async Task<string?> CheckRecipientAsync(SmtpTransaction transaction, string recipient, CancellationToken cancel)
    {
        if (await _delivery.ResolveAsync(recipient, null, cancel) is not null)
        {
            return null;
        }

        if (transaction.Kind == SmtpClientKind.Anonymous || transaction.TenantId is not long tenantId)
        {
            return $"554 5.7.1 <{recipient}>: Relay access denied";
        }

        transaction.CanRouteExternal ??= _config.Queue.AllowDirectDelivery
            || await _routing.ResolveAccountAsync(tenantId, transaction.Sender, transaction.Rule, cancel) is not null;
        return transaction.CanRouteExternal.Value
            ? null
            : $"550 5.4.4 <{recipient}>: Unable to route: no sending account is configured for this sender";
    }

    /// <summary>
    /// After DATA, for signed-in users: every address in From: (and Sender:) must be one the user may send as, so nobody can put
    /// somebody else's name on a message. Returns the refusing reply (or null) and the identity of the first From: address.
    /// </summary>
    public async Task<(string? Rejection, SendIdentity? FromIdentity)> CheckHeaderSendersAsync(MailUser user, byte[] raw, CancellationToken cancel)
    {
        HeaderList headers;
        using (var stream = new MemoryStream(raw, writable: false))
        {
            headers = await HeaderList.LoadAsync(ParserOptions.Default, stream, cancel);
        }

        SendIdentity? fromIdentity = null;
        bool hasFrom = false;
        foreach (Header header in headers.Where(h => h.Id is HeaderId.From or HeaderId.Sender))
        {
            if (!InternetAddressList.TryParse(ParserOptions.Default, header.RawValue, out InternetAddressList? list) || !list.Mailboxes.Any())
            {
                return ($"550 5.7.1 Message rejected: the {header.Field}: header cannot be read", null);
            }

            foreach (MailboxAddress mailbox in list.Mailboxes)
            {
                SendIdentity? identity = await _access.FindSendIdentityAsync(user, mailbox.Address, cancel);
                if (identity is null)
                {
                    return ($"550 5.7.1 Message rejected: {header.Field}: <{MailAddresses.Normalize(mailbox.Address)}> is not an address you may send as", null);
                }

                if (header.Id == HeaderId.From)
                {
                    hasFrom = true;
                    fromIdentity ??= identity;
                }
            }
        }

        return hasFrom ? (null, fromIdentity) : ("550 5.7.1 Message rejected: the message has no From: header", null);
    }
}

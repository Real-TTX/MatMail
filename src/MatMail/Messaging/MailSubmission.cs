using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>Wakes the outgoing-queue worker when something was queued (it also polls on its own).</summary>
public sealed class OutboundSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, int.MaxValue);

    public void Notify() => _semaphore.Release();

    /// <summary>Waits until something was queued or the timeout passed. Returns true when woken by a signal.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancel)
    {
        bool signalled = await _semaphore.WaitAsync(timeout, cancel);
        while (_semaphore.CurrentCount > 0 && await _semaphore.WaitAsync(0, cancel))
        {
            // Several signals count as one wake-up.
        }

        return signalled;
    }
}

/// <summary>Where an outgoing message came from, for the mail transfer log.</summary>
public sealed record TransferOrigin(TransferChannel Channel, string? Peer, string? RemoteIp);

/// <summary>The outgoing queue: messages waiting for delivery to external recipients.</summary>
public sealed class OutboundQueue
{
    private readonly MatMailDbContext _db;
    private readonly OutboundSignal _signal;
    private readonly TransferLog _transfers;

    public OutboundQueue(MatMailDbContext db, OutboundSignal signal, TransferLog transfers)
    {
        _db = db;
        _signal = signal;
        _transfers = transfers;
    }

    public async Task<OutboundMessage> EnqueueAsync(
        long tenantId, long? accountId, string envelopeFrom, IEnumerable<string> recipients, byte[] raw, string subject, long? mailboxId, long? senderUserId,
        TransferOrigin? origin = null, CancellationToken cancel = default)
    {
        var message = new OutboundMessage
        {
            TenantId = tenantId,
            MailAccountId = accountId,
            EnvelopeFrom = envelopeFrom,
            Recipients = recipients.Select(MailAddresses.Normalize).Distinct().ToArray(),
            Raw = raw,
            SizeBytes = raw.LongLength,
            Subject = subject.Length > 1000 ? subject[..1000] : subject,
            MailboxId = mailboxId,
            SenderUserId = senderUserId,
            Status = OutboundStatus.Pending,
            NextAttemptDate = DateTime.UtcNow,
        };

        _db.OutboundMessages.Add(message);
        await _db.SaveChangesAsync(cancel);
        _signal.Notify();

        await _transfers.RecordAsync(
            TransferDirection.Outbound, origin?.Channel ?? TransferChannel.System, TransferStatus.Queued, envelopeFrom, message.Recipients, message.Subject, raw.LongLength,
            tenantId, RawHeaders.MessageId(raw), origin?.Peer, origin?.RemoteIp, outboundMessageId: message.Id);
        return message;
    }

    /// <summary>Puts a failed or waiting message back in line, to be tried now.</summary>
    public async Task<bool> RetryNowAsync(long id, CancellationToken cancel = default)
    {
        int changed = await _db.OutboundMessages
            .Where(o => o.Id == id && (o.Status == OutboundStatus.Failed || o.Status == OutboundStatus.Pending || o.Status == OutboundStatus.Cancelled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OutboundStatus.Pending)
                .SetProperty(o => o.NextAttemptDate, DateTime.UtcNow), cancel);
        if (changed > 0)
        {
            _signal.Notify();
        }

        return changed > 0;
    }

    public async Task<bool> CancelAsync(long id, CancellationToken cancel = default)
        => await _db.OutboundMessages.Where(o => o.Id == id && o.Status == OutboundStatus.Pending)
               .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OutboundStatus.Cancelled), cancel) > 0;
}

/// <summary>Where a message comes from; signatures and templates treat the sources differently.</summary>
public enum SubmissionSource
{
    /// <summary>A mail program (Outlook, Thunderbird, a phone) signed in to the SMTP server.</summary>
    MailProgram,

    /// <summary>The web client: the writer chose the signature in the editor.</summary>
    Web,

    /// <summary>A device or server in a trusted network (smart host), without signing in.</summary>
    SmartHost,
}

/// <summary>What is to be sent.</summary>
public sealed record SubmissionRequest
{
    public SubmissionSource Source { get; init; } = SubmissionSource.MailProgram;

    /// <summary>The message (may contain Bcc; it is removed from what recipients get).</summary>
    public required byte[] Raw { get; init; }
    public required string EnvelopeFrom { get; init; }

    /// <summary>All recipients (To, Cc and Bcc).</summary>
    public required IReadOnlyList<string> Recipients { get; init; }
    public required long TenantId { get; init; }

    /// <summary>The mailbox the message is sent from: its Sent folder gets a copy when <see cref="SaveToSent"/> is set.</summary>
    public long? MailboxId { get; init; }
    public long? SenderUserId { get; init; }

    /// <summary>The smart-host rule when a trusted network sent it without signing in.</summary>
    public RelayRule? Rule { get; init; }
    public bool SaveToSent { get; init; }

    /// <summary>Put the message into its template and add the signature and footers that apply (false: it goes out as it is).</summary>
    public bool ApplyFooters { get; init; } = true;

    /// <summary>Who sent it (the signed-in user, the smart-host rule), for the mail transfer log.</summary>
    public string? Peer { get; init; }

    /// <summary>The address of the client, for the mail transfer log.</summary>
    public string? RemoteIp { get; init; }
}

public sealed record SubmissionResult(bool Accepted, string? Error, int LocalCopies, int Queued, IReadOnlyList<string> Rejected);

/// <summary>
/// Sending: takes a finished message and distributes it. Recipients on registered domains are delivered locally at once, all
/// others are queued for the provider account (or direct delivery) the sender is routed to. Templates, signatures and footers are
/// applied here, so every path (web client, SMTP submission, smart host) gets them.
/// </summary>
public sealed class MailSubmission
{
    private readonly MatMailDbContext _db;
    private readonly MailDelivery _delivery;
    private readonly SendRouting _routing;
    private readonly OutboundQueue _queue;
    private readonly SignatureService _signatures;
    private readonly TemplateService _templates;
    private readonly MailStore _store;
    private readonly FolderService _folders;
    private readonly AppConfig _config;
    private readonly IServiceScopeFactory _scopes;
    private readonly BrandingService _branding;

    public MailSubmission(
        MatMailDbContext db, MailDelivery delivery, SendRouting routing, OutboundQueue queue, SignatureService signatures, TemplateService templates,
        MailStore store, FolderService folders, AppConfig config, IServiceScopeFactory scopes, BrandingService branding)
    {
        _db = db;
        _delivery = delivery;
        _routing = routing;
        _queue = queue;
        _signatures = signatures;
        _templates = templates;
        _store = store;
        _folders = folders;
        _config = config;
        _scopes = scopes;
        _branding = branding;
    }

    public async Task<SubmissionResult> SubmitAsync(SubmissionRequest request, CancellationToken cancel = default)
    {
        MimeMessage message;
        using (var stream = new MemoryStream(request.Raw, writable: false))
        {
            message = await MimeMessage.LoadAsync(ParserOptions.Default, stream, cancel);
        }

        string fromDomain = MailAddresses.DomainOf(MailAddresses.Normalize(request.EnvelopeFrom));
        if (string.IsNullOrEmpty(message.MessageId))
        {
            message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(fromDomain.Length > 0 ? fromDomain : _config.Server.Hostname);
        }

        if (message.Date == DateTimeOffset.MinValue)
        {
            message.Date = DateTimeOffset.UtcNow;
        }

        if (request.ApplyFooters)
        {
            // The template first (it may turn a plain-text message into an HTML one), then the signature and the footers into it.
            SignatureContext context = await BuildContextAsync(request, message, cancel);
            await _templates.ApplyAsync(message, request.Source, request.TenantId, request.MailboxId, request.SenderUserId, request.Rule?.Id, context, cancel);
            await _signatures.ApplyAsync(message, request.Source, request.TenantId, request.MailboxId, request.SenderUserId, context, cancel);
        }

        byte[] withBcc = MimeSerializer.ToBytes(message);
        message.Bcc.Clear();
        byte[] clean = MimeSerializer.ToBytes(message);

        var local = new List<string>();
        var external = new List<string>();
        foreach (string recipient in request.Recipients.Select(MailAddresses.Normalize).Where(MailAddresses.IsValid).Distinct())
        {
            if (await _delivery.ResolveAsync(recipient, null, cancel) is not null)
            {
                local.Add(recipient);
            }
            else
            {
                external.Add(recipient);
            }
        }

        TransferChannel channel = request.Source switch
        {
            SubmissionSource.Web => TransferChannel.WebClient,
            SubmissionSource.SmartHost => TransferChannel.SmartHost,
            _ => TransferChannel.SmtpSubmission,
        };

        var rejected = new List<string>();
        int localCopies = 0;
        if (local.Count > 0)
        {
            // Addresses are unique across the server, so a recipient may live in another tenant than the sender: the delivery
            // runs as the system (the sender's scope only sees and may only write the sender's own tenant).
            using IServiceScope delivery = _scopes.CreateScope();
            delivery.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            DeliveryResult delivered = await delivery.ServiceProvider.GetRequiredService<MailDelivery>()
                .DeliverAsync(clean, new DeliverySource
                {
                    EnvelopeRecipients = local,
                    Channel = channel,
                    Peer = request.Peer,
                    RemoteIp = request.RemoteIp,
                    EnvelopeSender = request.EnvelopeFrom,
                }, cancel);
            localCopies = delivered.Delivered;
        }

        int queued = 0;
        if (external.Count > 0)
        {
            MailAccount? account = await _routing.ResolveAccountAsync(request.TenantId, request.EnvelopeFrom, request.Rule, cancel);
            if (account is null && !_config.Queue.AllowDirectDelivery)
            {
                return new SubmissionResult(false, "No sending account is configured for this sender and direct delivery is switched off.", localCopies, 0, external);
            }

            await _queue.EnqueueAsync(
                request.TenantId, account?.Id, request.EnvelopeFrom, external, clean, message.Subject ?? string.Empty, request.MailboxId, request.SenderUserId,
                new TransferOrigin(channel, request.Peer, request.RemoteIp), cancel);
            queued = 1;
        }

        if (request.SaveToSent && request.MailboxId is long mailboxId)
        {
            MailFolder? sent = await _folders.FindByKindAsync(mailboxId, FolderKind.Sent, cancel);
            if (sent is not null)
            {
                await _store.AddAsync(sent.Id, new NewMessage(withBcc) { IsRead = true }, cancel);
            }
        }

        return new SubmissionResult(true, null, localCopies, queued, rejected);
    }

    private async Task<SignatureContext> BuildContextAsync(SubmissionRequest request, MimeMessage message, CancellationToken cancel)
    {
        User? user = request.SenderUserId is long userId
            ? await _db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancel)
            : null;
        string tenant = await _db.Tenants.AsNoTracking().Where(t => t.Id == request.TenantId).Select(t => t.Name).FirstOrDefaultAsync(cancel) ?? string.Empty;
        MailboxAddress? from = message.From.Mailboxes.FirstOrDefault();
        Brand brand = await _branding.GetAsync(request.TenantId, cancel);
        return SignatureContext.For(user, from?.Name ?? string.Empty, from?.Address ?? request.EnvelopeFrom, tenant, brand.Website);
    }
}

/// <summary>Serialises messages the way mail travels: CRLF line ends.</summary>
public static class MimeSerializer
{
    public static byte[] ToBytes(MimeMessage message)
    {
        FormatOptions options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        options.EnsureNewLine = true;

        using var stream = new MemoryStream();
        message.WriteTo(options, stream);
        return stream.ToArray();
    }
}

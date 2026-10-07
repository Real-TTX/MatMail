using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using MimeKit;

namespace MatMail.MailServer.Outbound;

public enum RecipientState
{
    Delivered,

    /// <summary>4xx, time-outs, connection problems: worth another try later.</summary>
    TemporaryFailure,

    /// <summary>5xx: the receiving side refuses for good.</summary>
    PermanentFailure,
}

/// <summary>What happened with one recipient of a queued message; <see cref="Detail"/> is the server's answer or the problem.</summary>
public sealed record RecipientOutcome(string Address, RecipientState State, string Detail);

/// <summary>
/// The outcomes of one delivery attempt, recorded as they become known: whatever was learnt survives a failure (or a shutdown)
/// halfway through, so a recipient that already got the message is never sent it again. The first outcome of a recipient counts.
/// </summary>
internal sealed class AttemptOutcomes
{
    private readonly Dictionary<string, RecipientOutcome> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecipientOutcome> _all = new();

    public IReadOnlyList<RecipientOutcome> All => _all;

    public void Record(RecipientOutcome outcome)
    {
        if (_byAddress.TryAdd(outcome.Address, outcome))
        {
            _all.Add(outcome);
        }
    }

    /// <summary>Gives every listed recipient that has no outcome yet this one.</summary>
    public void RecordRest(IEnumerable<string> recipients, RecipientState state, string detail)
    {
        foreach (string recipient in recipients)
        {
            Record(new RecipientOutcome(recipient, state, detail));
        }
    }
}

/// <summary>
/// Sends a queued message over SMTP — through the provider account it is routed to, or directly to the recipients' mail
/// servers (MX) — and records the outcome per recipient.
/// </summary>
internal sealed class OutboundTransport
{
    private readonly ProviderConnector _connector;
    private readonly AppConfig _config;
    private readonly IMxResolver _mx;
    private readonly OutboundWorkerOptions _options;

    public OutboundTransport(ProviderConnector connector, AppConfig config, IMxResolver mx, OutboundWorkerOptions options)
    {
        _connector = connector;
        _config = config;
        _mx = mx;
        _options = options;
    }

    /// <summary>Readable route for the log: the account's name or "direct delivery".</summary>
    public static string DescribeRoute(OutboundMessage message)
        => message.MailAccountId is null ? "direct delivery" : $"account '{message.MailAccount?.Name ?? message.MailAccountId.ToString()}'";

    public async Task SendAsync(OutboundMessage message, MimeMessage mime, IReadOnlyList<string> recipients, AttemptOutcomes outcomes, CancellationToken cancel)
    {
        if (message.MailAccountId is not null)
        {
            await SendThroughAccountAsync(message.MailAccount, message.EnvelopeFrom, mime, recipients, outcomes, cancel);
            return;
        }

        if (!_config.Queue.AllowDirectDelivery)
        {
            outcomes.RecordRest(recipients, RecipientState.TemporaryFailure, "No sending account is set for this message and direct delivery is switched off.");
            return;
        }

        foreach (IGrouping<string, string> domain in recipients.GroupBy(MailAddresses.DomainOf, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await SendToDomainAsync(domain.Key, domain.ToList(), message.EnvelopeFrom, mime, outcomes, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                // One domain's problem must not cost the outcomes of the others.
                outcomes.RecordRest(domain, RecipientState.TemporaryFailure, $"{domain.Key}: {ex.Message}");
            }
        }
    }

    private async Task SendThroughAccountAsync(
        MailAccount? account, string envelopeFrom, MimeMessage mime, IReadOnlyList<string> recipients, AttemptOutcomes outcomes, CancellationToken cancel)
    {
        if (account is null)
        {
            outcomes.RecordRest(recipients, RecipientState.TemporaryFailure, "The sending account of this message does not exist any more.");
            return;
        }

        if (!account.IsEnabled || string.IsNullOrWhiteSpace(account.SendHost))
        {
            outcomes.RecordRest(recipients, RecipientState.TemporaryFailure, $"The sending account '{account.Name}' is disabled or has no SMTP server.");
            return;
        }

        SmtpClient client;
        try
        {
            client = await _connector.ConnectSmtpAsync(account, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            outcomes.RecordRest(recipients, RecipientState.TemporaryFailure, ProviderConnector.Describe(ex, account.SendHost));
            return;
        }

        using (client)
        {
            await SmtpSendLoop.SendAsync(client, mime, envelopeFrom, account.Address, recipients, account.SendHost, outcomes, cancel);
            await DisconnectQuietlyAsync(client);
        }
    }

    /// <summary>Direct delivery to one domain: its MX hosts in order until one answers.</summary>
    private async Task SendToDomainAsync(
        string domain, IReadOnlyList<string> recipients, string envelopeFrom, MimeMessage mime, AttemptOutcomes outcomes, CancellationToken cancel)
    {
        MxLookup lookup = await _mx.ResolveAsync(domain, cancel);
        if (lookup.Error is not null)
        {
            outcomes.RecordRest(recipients, lookup.IsPermanent ? RecipientState.PermanentFailure : RecipientState.TemporaryFailure, lookup.Error);
            return;
        }

        string lastError = $"No mail server of {domain} could be reached.";
        foreach (string host in lookup.Hosts)
        {
            (SmtpClient? client, string? error) = await ConnectDirectAsync(host, cancel);
            if (client is null)
            {
                lastError = error ?? lastError;
                continue;
            }

            using (client)
            {
                await SmtpSendLoop.SendAsync(client, mime, envelopeFrom, null, recipients, host, outcomes, cancel);
                await DisconnectQuietlyAsync(client);
                return;
            }
        }

        outcomes.RecordRest(recipients, RecipientState.TemporaryFailure, lastError);
    }

    /// <summary>
    /// Connects to a receiving mail server on port 25 the way MTAs do: STARTTLS when offered, any certificate accepted
    /// (opportunistic encryption); when the TLS handshake fails, once more without TLS.
    /// </summary>
    private async Task<(SmtpClient? Client, string? Error)> ConnectDirectAsync(string host, CancellationToken cancel)
    {
        string? error = null;
        foreach (SecureSocketOptions security in new[] { SecureSocketOptions.StartTlsWhenAvailable, SecureSocketOptions.None })
        {
            var client = new SmtpClient
            {
                Timeout = (int)_options.DirectTimeout.TotalMilliseconds,
                LocalDomain = _config.Server.Hostname,
                CheckCertificateRevocation = false,
                ServerCertificateValidationCallback = (_, _, _, _) => true,
            };

            try
            {
                await client.ConnectAsync(host, _options.DirectPort, security, cancel);
                return (client, null);
            }
            catch (SslHandshakeException ex)
            {
                client.Dispose();
                error = ProviderConnector.Describe(ex, host);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                client.Dispose();
                return (null, ProviderConnector.Describe(ex, host));
            }
        }

        return (null, error);
    }

    private static async Task DisconnectQuietlyAsync(SmtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.DisconnectAsync(true, timer.Token);
            }
        }
        catch (Exception)
        {
            // The message is out (or not) already; a failing QUIT changes nothing.
        }
    }
}

/// <summary>
/// One SMTP transaction per round on an open connection. A recipient the server refuses is taken out and the rest is sent again
/// (MailKit resets the transaction after a refused command), so the message data goes out once, to the accepted recipients only.
/// </summary>
internal static class SmtpSendLoop
{
    public static async Task SendAsync(
        SmtpClient client, MimeMessage message, string envelopeFrom, string? fallbackSender, IReadOnlyList<string> recipients, string host,
        AttemptOutcomes outcomes, CancellationToken cancel)
    {
        var pending = new List<(string Address, MailboxAddress Mailbox)>();
        foreach (string recipient in recipients)
        {
            if (TryMailbox(recipient, out MailboxAddress? mailbox))
            {
                pending.Add((recipient, mailbox));
            }
            else
            {
                outcomes.Record(new RecipientOutcome(recipient, RecipientState.PermanentFailure, "The recipient address is not valid."));
            }
        }

        if (!TryMailbox(envelopeFrom, out MailboxAddress? sender))
        {
            outcomes.RecordRest(pending.Select(p => p.Address), RecipientState.PermanentFailure, "The sender address is not valid.");
            return;
        }

        bool fallbackTried = false;
        while (pending.Count > 0)
        {
            if (!client.IsConnected)
            {
                outcomes.RecordRest(pending.Select(p => p.Address), RecipientState.TemporaryFailure, $"{host}: the connection was closed.");
                return;
            }

            try
            {
                string response = await client.SendAsync(FormatOptions.Default, message, sender, pending.Select(p => p.Mailbox), cancel);
                outcomes.RecordRest(pending.Select(p => p.Address), RecipientState.Delivered, $"{host}: {response}".Trim());
                return;
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && IndexOf(pending, ex.Mailbox) is int index and >= 0)
            {
                outcomes.Record(new RecipientOutcome(pending[index].Address, StateOf(ex.StatusCode), Describe(host, ex)));
                pending.RemoveAt(index);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.SenderNotAccepted && !fallbackTried
                                                  && MailAddresses.IsValid(fallbackSender)
                                                  && !string.Equals(fallbackSender, sender.Address, StringComparison.OrdinalIgnoreCase)
                                                  && TryMailbox(fallbackSender!, out MailboxAddress? fallback))
            {
                // Many providers only take the address of the signed-in account as envelope sender; the From: header stays.
                fallbackTried = true;
                sender = fallback;
            }
            catch (SmtpCommandException ex)
            {
                outcomes.RecordRest(pending.Select(p => p.Address), StateOf(ex.StatusCode), Describe(host, ex));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                outcomes.RecordRest(pending.Select(p => p.Address), RecipientState.TemporaryFailure, ProviderConnector.Describe(ex, host));
                return;
            }
        }
    }

    private static RecipientState StateOf(SmtpStatusCode code)
        => (int)code >= 500 ? RecipientState.PermanentFailure : RecipientState.TemporaryFailure;

    private static string Describe(string host, SmtpCommandException ex) => $"{host}: {(int)ex.StatusCode} {ex.Message}".Trim();

    private static int IndexOf(List<(string Address, MailboxAddress Mailbox)> pending, MailboxAddress? mailbox)
    {
        if (mailbox is null)
        {
            return -1;
        }

        int index = pending.FindIndex(p => ReferenceEquals(p.Mailbox, mailbox));
        return index >= 0 ? index : pending.FindIndex(p => string.Equals(p.Mailbox.Address, mailbox.Address, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An envelope address for MailKit; the empty string stands for the null sender "&lt;&gt;" (bounces).</summary>
    private static bool TryMailbox(string address, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MailboxAddress? mailbox)
    {
        mailbox = null;
        try
        {
            mailbox = new MailboxAddress(string.Empty, address.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException)
        {
            return false;
        }
    }
}

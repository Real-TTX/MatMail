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
/// Sends a queued message over SMTP — through the provider account it is routed to, or directly to the recipients' mail
/// servers (MX) — and reports the outcome per recipient.
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

    public async Task<IReadOnlyList<RecipientOutcome>> SendAsync(OutboundMessage message, MimeMessage mime, IReadOnlyList<string> recipients, CancellationToken cancel)
    {
        if (message.MailAccountId is not null)
        {
            return await SendThroughAccountAsync(message.MailAccount, message.EnvelopeFrom, mime, recipients, cancel);
        }

        if (!_config.Queue.AllowDirectDelivery)
        {
            return Fail(recipients, RecipientState.TemporaryFailure, "No sending account is set for this message and direct delivery is switched off.");
        }

        var outcomes = new List<RecipientOutcome>();
        foreach (IGrouping<string, string> domain in recipients.GroupBy(MailAddresses.DomainOf, StringComparer.OrdinalIgnoreCase))
        {
            outcomes.AddRange(await SendToDomainAsync(domain.Key, domain.ToList(), message.EnvelopeFrom, mime, cancel));
        }

        return outcomes;
    }

    internal static IReadOnlyList<RecipientOutcome> Fail(IEnumerable<string> recipients, RecipientState state, string detail)
        => recipients.Select(r => new RecipientOutcome(r, state, detail)).ToList();

    private async Task<IReadOnlyList<RecipientOutcome>> SendThroughAccountAsync(
        MailAccount? account, string envelopeFrom, MimeMessage mime, IReadOnlyList<string> recipients, CancellationToken cancel)
    {
        if (account is null)
        {
            return Fail(recipients, RecipientState.TemporaryFailure, "The sending account of this message does not exist any more.");
        }

        if (!account.IsEnabled || string.IsNullOrWhiteSpace(account.SendHost))
        {
            return Fail(recipients, RecipientState.TemporaryFailure, $"The sending account '{account.Name}' is disabled or has no SMTP server.");
        }

        SmtpClient client;
        try
        {
            client = await _connector.ConnectSmtpAsync(account, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            return Fail(recipients, RecipientState.TemporaryFailure, ProviderConnector.Describe(ex, account.SendHost));
        }

        using (client)
        {
            IReadOnlyList<RecipientOutcome> outcomes = await SmtpSendLoop.SendAsync(client, mime, envelopeFrom, account.Address, recipients, account.SendHost, cancel);
            await DisconnectQuietlyAsync(client);
            return outcomes;
        }
    }

    /// <summary>Direct delivery to one domain: its MX hosts in order until one answers.</summary>
    private async Task<IReadOnlyList<RecipientOutcome>> SendToDomainAsync(
        string domain, IReadOnlyList<string> recipients, string envelopeFrom, MimeMessage mime, CancellationToken cancel)
    {
        MxLookup lookup = await _mx.ResolveAsync(domain, cancel);
        if (lookup.Error is not null)
        {
            return Fail(recipients, lookup.IsPermanent ? RecipientState.PermanentFailure : RecipientState.TemporaryFailure, lookup.Error);
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
                IReadOnlyList<RecipientOutcome> outcomes = await SmtpSendLoop.SendAsync(client, mime, envelopeFrom, null, recipients, host, cancel);
                await DisconnectQuietlyAsync(client);
                return outcomes;
            }
        }

        return Fail(recipients, RecipientState.TemporaryFailure, lastError);
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
    public static async Task<IReadOnlyList<RecipientOutcome>> SendAsync(
        SmtpClient client, MimeMessage message, string envelopeFrom, string? fallbackSender, IReadOnlyList<string> recipients, string host,
        CancellationToken cancel)
    {
        var outcomes = new List<RecipientOutcome>();
        var pending = new List<(string Address, MailboxAddress Mailbox)>();
        foreach (string recipient in recipients)
        {
            if (TryMailbox(recipient, out MailboxAddress? mailbox))
            {
                pending.Add((recipient, mailbox));
            }
            else
            {
                outcomes.Add(new RecipientOutcome(recipient, RecipientState.PermanentFailure, "The recipient address is not valid."));
            }
        }

        if (!TryMailbox(envelopeFrom, out MailboxAddress? sender))
        {
            outcomes.AddRange(pending.Select(p => new RecipientOutcome(p.Address, RecipientState.PermanentFailure, "The sender address is not valid.")));
            return outcomes;
        }

        bool fallbackTried = false;
        while (pending.Count > 0)
        {
            if (!client.IsConnected)
            {
                outcomes.AddRange(pending.Select(p => new RecipientOutcome(p.Address, RecipientState.TemporaryFailure, $"{host}: the connection was closed.")));
                break;
            }

            try
            {
                string response = await client.SendAsync(FormatOptions.Default, message, sender, pending.Select(p => p.Mailbox), cancel);
                outcomes.AddRange(pending.Select(p => new RecipientOutcome(p.Address, RecipientState.Delivered, $"{host}: {response}".Trim())));
                break;
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && IndexOf(pending, ex.Mailbox) is int index and >= 0)
            {
                outcomes.Add(new RecipientOutcome(pending[index].Address, StateOf(ex.StatusCode), Describe(host, ex)));
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
                outcomes.AddRange(pending.Select(p => new RecipientOutcome(p.Address, StateOf(ex.StatusCode), Describe(host, ex))));
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                outcomes.AddRange(pending.Select(p => new RecipientOutcome(p.Address, RecipientState.TemporaryFailure, ProviderConnector.Describe(ex, host))));
                break;
            }
        }

        return outcomes;
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

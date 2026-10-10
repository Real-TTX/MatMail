using System.Text;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;

namespace MatMail.MailServer.Smtp;

/// <summary>
/// What happens with a message once DATA is complete: anonymous mail from other servers is delivered into the local mailboxes,
/// mail of signed-in users and trusted networks is submitted (local recipients at once, external ones through the outgoing
/// queue, footers appended). Delivery may write into any tenant's mailboxes, so it runs as the system in a fresh scope.
/// </summary>
internal sealed class SmtpMessageHandler
{
    /// <summary>A message with this many Received headers is going round in circles (RFC 5321 6.3).</summary>
    public const int MaxHops = 50;

    private readonly IServiceScopeFactory _scopes;

    public SmtpMessageHandler(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Delivers or submits the (already stamped) message and returns the reply for the client.</summary>
    public async Task<string> HandleAsync(SmtpTransaction transaction, byte[] raw, long? mailboxId, string queueId, string? remoteIp, CancellationToken cancel)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();

        if (transaction.Kind == SmtpClientKind.Anonymous)
        {
            DeliveryResult delivered;
            try
            {
                delivered = await scope.ServiceProvider.GetRequiredService<MailDelivery>()
                    .DeliverAsync(raw, new DeliverySource
                    {
                        EnvelopeRecipients = transaction.Recipients,
                        Channel = TransferChannel.SmtpServer,
                        RemoteIp = remoteIp,
                        EnvelopeSender = transaction.Sender,
                    }, cancel);
            }
            catch (MailboxFullException)
            {
                // One of the mailboxes filled up since RCPT TO (nothing was stored): the sender tries the whole message again later.
                return "452 4.2.2 Mailbox full, try again later";
            }

            return delivered.Copies.Count > 0
                ? Queued(queueId)
                : "451 4.3.0 The recipients cannot be resolved right now, please try again later";
        }

        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = raw,
            EnvelopeFrom = transaction.Sender,
            Recipients = transaction.Recipients,
            TenantId = transaction.TenantId ?? throw new InvalidOperationException("A submission needs a tenant."),
            MailboxId = mailboxId,
            SenderUserId = transaction.User?.UserId,
            Rule = transaction.Rule,
            Source = transaction.User is null ? SubmissionSource.SmartHost : SubmissionSource.MailProgram,
            Peer = transaction.User?.LoginName ?? transaction.Rule?.Name,
            RemoteIp = remoteIp,

            // Mail clients keep their own copy in "Sent".
            SaveToSent = false,
            ApplyFooters = true,
        }, cancel);

        return result.Accepted ? Queued(queueId) : result.Temporary ? $"452 4.2.2 {result.Error}" : $"554 5.4.4 {result.Error ?? "The message cannot be sent"}";
    }

    private static string Queued(string queueId) => $"250 2.0.0 OK queued as {queueId}";

    /// <summary>
    /// Puts Return-Path and Received in front of the message. Return-Path lines the client sent are removed first: only the
    /// server that delivers decides the return path (RFC 5321 section 4.4).
    /// </summary>
    public static byte[] AddTraceHeaders(byte[] raw, string returnPath, string received)
    {
        byte[] body = RemoveHeader(raw, "Return-Path");
        byte[] prefix = Encoding.UTF8.GetBytes($"Return-Path: <{returnPath}>\r\n{received}\r\n");
        byte[] result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result, prefix.Length);
        return result;
    }

    /// <summary>Removes every occurrence of a header field (with its folded continuation lines) from the header block.</summary>
    internal static byte[] RemoveHeader(byte[] raw, string fieldName)
    {
        int headerEnd = FindHeaderEnd(raw);
        byte[] prefix = Encoding.ASCII.GetBytes(fieldName + ":");
        using var result = new MemoryStream(raw.Length);
        int position = 0;
        bool skipping = false;
        bool removedAny = false;

        while (position < headerEnd)
        {
            int newline = Array.IndexOf(raw, (byte)'\n', position, headerEnd - position);
            int lineEnd = newline < 0 ? headerEnd : newline + 1;
            ReadOnlySpan<byte> line = raw.AsSpan(position, lineEnd - position);

            bool continuation = line[0] is (byte)' ' or (byte)'\t';
            if (!continuation)
            {
                skipping = StartsWithField(line, prefix);
                removedAny |= skipping;
            }

            if (!skipping)
            {
                result.Write(line);
            }

            position = lineEnd;
        }

        if (!removedAny)
        {
            return raw;
        }

        result.Write(raw.AsSpan(headerEnd));
        return result.ToArray();
    }

    /// <summary>How often a header field occurs in the header block (folded continuation lines are not counted).</summary>
    internal static int CountHeader(byte[] raw, string fieldName)
    {
        int headerEnd = FindHeaderEnd(raw);
        byte[] prefix = Encoding.ASCII.GetBytes(fieldName + ":");
        int count = 0;
        int position = 0;
        while (position < headerEnd)
        {
            int newline = Array.IndexOf(raw, (byte)'\n', position, headerEnd - position);
            int lineEnd = newline < 0 ? headerEnd : newline + 1;
            if (StartsWithField(raw.AsSpan(position, lineEnd - position), prefix))
            {
                count++;
            }

            position = lineEnd;
        }

        return count;
    }

    /// <summary>The index of the empty line that ends the header block (the length of the message when there is none).</summary>
    private static int FindHeaderEnd(byte[] raw)
    {
        int position = 0;
        while (position < raw.Length)
        {
            int newline = Array.IndexOf(raw, (byte)'\n', position);
            if (newline < 0)
            {
                return raw.Length;
            }

            int length = newline - position;
            if (length == 0 || (length == 1 && raw[position] == (byte)'\r'))
            {
                return position;
            }

            position = newline + 1;
        }

        return raw.Length;
    }

    private static bool StartsWithField(ReadOnlySpan<byte> line, byte[] prefix)
    {
        if (line.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (char.ToLowerInvariant((char)line[i]) != char.ToLowerInvariant((char)prefix[i]))
            {
                return false;
            }
        }

        return true;
    }
}

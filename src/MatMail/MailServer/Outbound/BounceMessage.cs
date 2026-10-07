using System.Text;
using MatMail.Data;
using MatMail.Messaging;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace MatMail.MailServer.Outbound;

/// <summary>The "Undelivered Mail Returned to Sender" message: why it failed, for whom, and the headers of the original.</summary>
internal static class BounceMessage
{
    public const string Subject = "Undelivered Mail Returned to Sender";

    private const int MaxOriginalHeaderChars = 32 * 1024;

    public static byte[] Build(string hostname, OutboundMessage original, IReadOnlyList<RecipientOutcome> failed, bool expired, int maxAgeHours)
    {
        var text = new StringBuilder();
        text.Append("This is the mail system at host ").Append(hostname).Append(".\r\n\r\n");
        text.Append("I'm sorry to have to inform you that your message could not be delivered to one or more recipients.\r\n");
        if (expired)
        {
            text.Append("Delivery was tried for ").Append(maxAgeHours).Append(" hours and has been given up.\r\n");
        }

        text.Append("\r\n");
        foreach (RecipientOutcome failure in failed)
        {
            text.Append('<').Append(failure.Address).Append(">: ").Append(failure.Detail).Append("\r\n");
        }

        text.Append("\r\n--- Headers of the original message ---\r\n\r\n");
        text.Append(OriginalHeaders(original.Raw));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mail Delivery System", $"postmaster@{hostname}"));
        message.To.Add(new MailboxAddress(string.Empty, original.EnvelopeFrom));
        message.Subject = Subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeUtils.GenerateMessageId(hostname);

        // RFC 3834: an automatic answer, nobody should answer it automatically again.
        message.Headers.Add("Auto-Submitted", "auto-replied");
        message.Body = new TextPart(TextFormat.Plain) { Text = text.ToString() };
        message.Prepare(EncodingConstraint.EightBit);
        return MimeSerializer.ToBytes(message);
    }

    private static string OriginalHeaders(byte[] raw)
    {
        int end = raw.Length;
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n' && (raw[i + 1] == (byte)'\n' || (raw[i + 1] == (byte)'\r' && i + 2 < raw.Length && raw[i + 2] == (byte)'\n')))
            {
                end = i + 1;
                break;
            }
        }

        string headers = Encoding.UTF8.GetString(raw, 0, end);
        return headers.Length <= MaxOriginalHeaderChars ? headers : headers[..MaxOriginalHeaderChars] + "\r\n[...]\r\n";
    }
}

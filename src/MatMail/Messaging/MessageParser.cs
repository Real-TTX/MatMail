using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;


namespace MatMail.Messaging;

/// <summary>What the database stores about a message besides its bytes (subject, sender, preview, threading, ...).</summary>
public sealed class ParsedMessage
{
    public string Subject { get; init; } = string.Empty;
    public string FromName { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>"Name <address>" of the To and Cc recipients, comma separated, for lists and search.</summary>
    public string ToSummary { get; init; } = string.Empty;
    public DateTime? SentDate { get; init; }
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public string? References { get; init; }
    public string ThreadKey { get; init; } = string.Empty;
    public bool HasAttachments { get; init; }
    public string Preview { get; init; } = string.Empty;

    /// <summary>Plain text of the body (whitespace collapsed, truncated); what the body search looks at.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>The header block of the raw message (up to and including the blank line).</summary>
    public byte[] HeaderBytes { get; init; } = Array.Empty<byte>();

    /// <summary>Lower-case addresses of To, Cc and Bcc.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();

    /// <summary>Lower-case addresses a provider or server noted as the real recipient (Delivered-To, X-Original-To, Envelope-To, ...).</summary>
    public IReadOnlyList<string> DeliveredTo { get; init; } = Array.Empty<string>();
}

/// <summary>Reads a raw RFC 822 message with MimeKit and extracts the facts the mail store keeps.</summary>
public static partial class MessageParser
{
    // Column lengths of MailMessage: longer values would make the insert fail. Ids are capped as well because they are indexed.
    private const int MaxSubjectLength = 1000;
    private const int MaxFromNameLength = 500;
    private const int MaxAddressLength = 320;
    private const int MaxIdLength = 500;

    private const int PreviewLength = 200;
    private const int SearchTextLength = 100_000;

    private static readonly string[] DeliveredToHeaders = { "Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To", "X-Delivered-To" };

    public static ParsedMessage Parse(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        MimeMessage message = MimeMessage.Load(ParserOptions.Default, stream);
        return Parse(message, raw);
    }

    public static ParsedMessage Parse(MimeMessage message, byte[] raw)
    {
        MailboxAddress? from = message.From.Mailboxes.FirstOrDefault();
        string text = ExtractText(message);
        string collapsed = Whitespace().Replace(text, " ").Trim();

        string? messageId = Truncate(Clean(message.MessageId), MaxIdLength);
        string? inReplyTo = Truncate(Clean(message.InReplyTo), MaxIdLength);
        string references = string.Join(' ', message.References.Select(r => "<" + r.Trim('<', '>') + ">"));
        string subject = Truncate(message.Subject ?? string.Empty, MaxSubjectLength);

        return new ParsedMessage
        {
            Subject = subject,
            FromName = Truncate(from?.Name ?? string.Empty, MaxFromNameLength),
            FromAddress = Truncate((from?.Address ?? string.Empty).ToLowerInvariant(), MaxAddressLength),
            ToSummary = string.Join(", ", message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(Describe)),
            SentDate = message.Date == DateTimeOffset.MinValue ? null : message.Date.UtcDateTime,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            References = references.Length == 0 ? null : references,
            ThreadKey = Truncate(BuildThreadKey(message, subject, messageId, inReplyTo), MaxIdLength),
            HasAttachments = message.Attachments.Any(),
            Preview = collapsed.Length <= PreviewLength ? collapsed : collapsed[..PreviewLength],
            SearchText = collapsed.Length <= SearchTextLength ? collapsed : collapsed[..SearchTextLength],
            HeaderBytes = ExtractHeaderBytes(raw),
            Recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
                .Select(m => m.Address.ToLowerInvariant()).Distinct().ToList(),
            DeliveredTo = ReadDeliveredTo(message),
        };
    }

    /// <summary>The root message id of the conversation, or the normalised subject when the message has no references.</summary>
    private static string BuildThreadKey(MimeMessage message, string subject, string? messageId, string? inReplyTo)
    {
        string? root = message.References.FirstOrDefault() is { Length: > 0 } first
            ? "<" + first.Trim('<', '>') + ">"
            : inReplyTo ?? messageId;

        // Without any id the subject has to do.
        return root ?? "subj:" + NormalizeSubject(subject);
    }

    /// <summary>Lower-case subject without the "Re:", "Fwd:", "AW:", "WG:" prefixes.</summary>
    public static string NormalizeSubject(string? subject) => StripReplyPrefixes(subject).ToLowerInvariant();

    /// <summary>The subject without any "Re:", "Fwd:", "AW:", "WG:" prefixes, in its original case.</summary>
    public static string StripReplyPrefixes(string? subject)
    {
        string value = (subject ?? string.Empty).Trim();
        string previous;
        do
        {
            previous = value;
            value = SubjectPrefix().Replace(value, string.Empty).Trim();
        }
        while (value != previous);

        return value;
    }

    private static string ExtractText(MimeMessage message)
    {
        string? plain = message.TextBody;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            return plain;
        }

        string? html = message.HtmlBody;
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        try
        {
            return HtmlText.ToPlainText(html);
        }
        catch (Exception)
        {
            return Regex.Replace(html, "<[^>]+>", " ");
        }
    }

    private static byte[] ExtractHeaderBytes(byte[] raw)
    {
        // The header block ends with the first empty line (CRLF CRLF, or LF LF for bare-LF messages).
        for (int i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] == (byte)'\n' && raw[i + 1] == (byte)'\n')
            {
                return raw[..(i + 2)];
            }

            if (i + 3 < raw.Length && raw[i] == (byte)'\r' && raw[i + 1] == (byte)'\n' && raw[i + 2] == (byte)'\r' && raw[i + 3] == (byte)'\n')
            {
                return raw[..(i + 4)];
            }
        }

        return raw;
    }

    private static List<string> ReadDeliveredTo(MimeMessage message)
    {
        var result = new List<string>();
        foreach (string headerName in DeliveredToHeaders)
        {
            foreach (Header header in message.Headers.Where(h => string.Equals(h.Field, headerName, StringComparison.OrdinalIgnoreCase)))
            {
                if (MailboxAddress.TryParse(header.Value.Trim(), out MailboxAddress? address) && !string.IsNullOrEmpty(address.Address))
                {
                    result.Add(address.Address.ToLowerInvariant());
                }
                else if (InternetAddressList.TryParse(header.Value, out InternetAddressList? list))
                {
                    result.AddRange(list.Mailboxes.Select(m => m.Address.ToLowerInvariant()));
                }
            }
        }

        return result.Distinct().ToList();
    }

    private static string Describe(MailboxAddress mailbox)
        => string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>";

    /// <summary>Cuts a text to the length a column holds, without splitting a surrogate pair.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Truncate(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        int length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length];
    }

    private static string? Clean(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : "<" + id.Trim().Trim('<', '>') + ">";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(re|fwd?|aw|wg|antw|sv|vs|tr|rv)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectPrefix();
}

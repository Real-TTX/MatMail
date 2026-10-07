using System.Globalization;
using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Builds the IMAP ENVELOPE (RFC 3501, section 7.4.2) from a header block. Values stay as close to the header as possible; text that
/// is not 7-bit is RFC 2047-encoded, so the envelope can always be sent as quoted strings.
/// </summary>
internal static class ImapEnvelope
{
    private const string MissingDomain = "MISSING_DOMAIN";

    /// <summary>The ENVELOPE of a message, computed from its header bytes only (no body needed).</summary>
    public static string Build(byte[]? headerBytes) => Build(LoadHeaders(headerBytes));

    public static string Build(HeaderList headers)
    {
        string from = Addresses(headers, "From") ?? "NIL";
        var envelope = new StringBuilder(256);
        envelope.Append('(');
        envelope.Append(ImapFormat.NString(RawValue(headers, "Date"))).Append(' ');
        envelope.Append(ImapFormat.NString(TextValue(headers, "Subject"))).Append(' ');
        envelope.Append(from).Append(' ');
        envelope.Append(Addresses(headers, "Sender") ?? from).Append(' ');
        envelope.Append(Addresses(headers, "Reply-To") ?? from).Append(' ');
        envelope.Append(Addresses(headers, "To") ?? "NIL").Append(' ');
        envelope.Append(Addresses(headers, "Cc") ?? "NIL").Append(' ');
        envelope.Append(Addresses(headers, "Bcc") ?? "NIL").Append(' ');
        envelope.Append(ImapFormat.NString(RawValue(headers, "In-Reply-To"))).Append(' ');
        envelope.Append(ImapFormat.NString(RawValue(headers, "Message-ID")));
        envelope.Append(')');
        return envelope.ToString();
    }

    /// <summary>Parses a header block; a block MimeKit cannot read yields an empty list.</summary>
    public static HeaderList LoadHeaders(byte[]? headerBytes)
    {
        if (headerBytes is null || headerBytes.Length == 0)
        {
            return new HeaderList();
        }

        try
        {
            using var stream = new MemoryStream(headerBytes, writable: false);
            return HeaderList.Load(ParserOptions.Default, stream);
        }
        catch (Exception)
        {
            return new HeaderList();
        }
    }

    /// <summary>The unfolded value of the first header with this name, as it stands in the message (null when missing or empty).</summary>
    public static string? RawValue(HeaderList headers, string field)
    {
        Header? header = headers.FirstOrDefault(h => string.Equals(h.Field, field, StringComparison.OrdinalIgnoreCase));
        if (header is null)
        {
            return null;
        }

        string value = IsAscii(header.RawValue) ? Unfold(Encoding.ASCII.GetString(header.RawValue)) : header.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Unstructured text (Subject, Content-Description): kept as it is when 7-bit, otherwise RFC 2047-encoded.</summary>
    public static string? TextValue(HeaderList headers, string field)
    {
        Header? header = headers.FirstOrDefault(h => string.Equals(h.Field, field, StringComparison.OrdinalIgnoreCase));
        if (header is null)
        {
            return null;
        }

        if (IsAscii(header.RawValue))
        {
            return Unfold(Encoding.ASCII.GetString(header.RawValue));
        }

        return Encoding.ASCII.GetString(Rfc2047.EncodeText(Encoding.UTF8, header.Value.Trim()));
    }

    /// <summary>"((name adl mailbox host) ...)" or null when the header is missing, empty or unreadable.</summary>
    public static string? Addresses(HeaderList headers, string field)
    {
        Header? header = headers.FirstOrDefault(h => string.Equals(h.Field, field, StringComparison.OrdinalIgnoreCase));
        if (header is null || !InternetAddressList.TryParse(ParserOptions.Default, header.RawValue, out InternetAddressList? list) || list.Count == 0)
        {
            return null;
        }

        var result = new StringBuilder("(");
        foreach (InternetAddress address in list)
        {
            AppendAddress(result, address);
        }

        return result.Append(')').ToString();
    }

    private static void AppendAddress(StringBuilder result, InternetAddress address)
    {
        if (address is GroupAddress group)
        {
            // Group syntax: a start marker with the group name, the members, an end marker.
            result.Append("(NIL NIL ").Append(ImapFormat.String(EncodePhrase(group.Name) ?? string.Empty)).Append(" NIL)");
            foreach (InternetAddress member in group.Members)
            {
                AppendAddress(result, member);
            }

            result.Append("(NIL NIL NIL NIL)");
            return;
        }

        if (address is not MailboxAddress mailbox)
        {
            return;
        }

        string route = mailbox.Route.Count == 0 ? "NIL" : ImapFormat.String(string.Join(',', mailbox.Route.Select(d => "@" + d)));
        string domain = string.IsNullOrEmpty(mailbox.Domain) ? MissingDomain : ToAsciiDomain(mailbox.Domain);
        result.Append('(')
            .Append(ImapFormat.NString(EncodePhrase(mailbox.Name))).Append(' ')
            .Append(route).Append(' ')
            .Append(ImapFormat.String(mailbox.LocalPart ?? string.Empty)).Append(' ')
            .Append(ImapFormat.String(domain))
            .Append(')');
    }

    /// <summary>A display name as 7-bit text: unchanged when ASCII, otherwise as RFC 2047 encoded-words.</summary>
    private static string? EncodePhrase(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return name.All(c => c < 0x80) ? name : Encoding.ASCII.GetString(Rfc2047.EncodePhrase(Encoding.UTF8, name));
    }

    private static string ToAsciiDomain(string domain)
    {
        if (domain.All(c => c < 0x80))
        {
            return domain;
        }

        try
        {
            return new IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }

    /// <summary>Removes the line breaks of a folded header value and the surrounding white space.</summary>
    public static string Unfold(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

    private static bool IsAscii(byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            if (value >= 0x80)
            {
                return false;
            }
        }

        return true;
    }
}

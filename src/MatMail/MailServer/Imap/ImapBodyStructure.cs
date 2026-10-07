using System.Text;
using MimeKit;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Builds BODYSTRUCTURE (with extension data) and BODY (without) as defined in RFC 3501, section 7.4.2, from a parsed message.
/// </summary>
internal static class ImapBodyStructure
{
    public static string Build(ImapMessageStructure structure, bool extensible)
    {
        var result = new StringBuilder(512);
        AppendPart(result, structure.Root.Body, extensible);
        return result.ToString();
    }

    private static void AppendPart(StringBuilder result, ImapBodyPart part, bool extensible)
    {
        if (part.Entity is Multipart multipart)
        {
            AppendMultipart(result, part, multipart, extensible);
        }
        else
        {
            AppendSinglePart(result, part, extensible);
        }
    }

    private static void AppendMultipart(StringBuilder result, ImapBodyPart part, Multipart multipart, bool extensible)
    {
        result.Append('(');
        if (part.Children.Count == 0)
        {
            // The grammar needs at least one part; an empty multipart gets an empty text part.
            result.Append("(\"text\" \"plain\" (\"charset\" \"us-ascii\") NIL NIL \"7bit\" 0 0)");
        }

        foreach (ImapBodyPart child in part.Children)
        {
            AppendPart(result, child, extensible);
        }

        result.Append(' ').Append(ImapFormat.String(multipart.ContentType.MediaSubtype));
        if (extensible)
        {
            result.Append(' ').Append(Parameters(multipart.ContentType.Parameters));
            AppendCommonExtensions(result, part.Entity);
        }

        result.Append(')');
    }

    private static void AppendSinglePart(StringBuilder result, ImapBodyPart part, bool extensible)
    {
        MimeEntity entity = part.Entity;
        bool hasContentType = entity.Headers.Contains(HeaderId.ContentType);
        string type = hasContentType ? entity.ContentType.MediaType : "text";
        string subtype = hasContentType ? entity.ContentType.MediaSubtype : "plain";
        string parameters = hasContentType ? Parameters(entity.ContentType.Parameters) : "(\"charset\" \"us-ascii\")";

        result.Append('(')
            .Append(ImapFormat.String(type)).Append(' ')
            .Append(ImapFormat.String(subtype)).Append(' ')
            .Append(parameters).Append(' ')
            .Append(ImapFormat.NString(ImapEnvelope.RawValue(entity.Headers, "Content-ID"))).Append(' ')
            .Append(ImapFormat.NString(ImapEnvelope.TextValue(entity.Headers, "Content-Description"))).Append(' ')
            .Append(ImapFormat.String(TransferEncoding(entity))).Append(' ')
            .Append(part.Size);

        bool isEmbeddedMessage = part.Message is not null && type.Equals("message", StringComparison.OrdinalIgnoreCase)
                                 && subtype.Equals("rfc822", StringComparison.OrdinalIgnoreCase);
        if (isEmbeddedMessage)
        {
            result.Append(' ').Append(ImapEnvelope.Build(part.Message!.Message.Headers)).Append(' ');
            AppendPart(result, part.Message.Body, extensible);
            result.Append(' ').Append(part.Lines);
        }
        else if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            result.Append(' ').Append(part.Lines);
        }

        if (extensible)
        {
            result.Append(' ').Append(ImapFormat.NString(ImapEnvelope.RawValue(entity.Headers, "Content-MD5")));
            AppendCommonExtensions(result, entity);
        }

        result.Append(')');
    }

    /// <summary>body-fld-dsp SP body-fld-lang SP body-fld-loc.</summary>
    private static void AppendCommonExtensions(StringBuilder result, MimeEntity entity)
    {
        ContentDisposition? disposition = entity.Headers.Contains(HeaderId.ContentDisposition) ? entity.ContentDisposition : null;
        result.Append(' ');
        if (disposition is null)
        {
            result.Append("NIL");
        }
        else
        {
            result.Append('(').Append(ImapFormat.String(disposition.Disposition)).Append(' ').Append(Parameters(disposition.Parameters)).Append(')');
        }

        result.Append(' ').Append(Languages(ImapEnvelope.RawValue(entity.Headers, "Content-Language")));
        result.Append(' ').Append(ImapFormat.NString(ImapEnvelope.RawValue(entity.Headers, "Content-Location")));
    }

    /// <summary>("name" "value" ...) or NIL. Values that are not ASCII are written in RFC 2231 form ("name*" "utf-8''%C3%A4.pdf").</summary>
    private static string Parameters(ParameterList parameters)
    {
        if (parameters.Count == 0)
        {
            return "NIL";
        }

        var result = new StringBuilder("(");
        foreach (Parameter parameter in parameters)
        {
            if (result.Length > 1)
            {
                result.Append(' ');
            }

            string value = parameter.Value ?? string.Empty;
            if (value.All(c => c < 0x80 && c != '\r' && c != '\n'))
            {
                result.Append(ImapFormat.String(parameter.Name)).Append(' ').Append(ImapFormat.String(value));
            }
            else
            {
                result.Append(ImapFormat.String(parameter.Name + "*")).Append(' ').Append(ImapFormat.String("utf-8''" + PercentEncode(value)));
            }
        }

        return result.Append(')').ToString();
    }

    private static string Languages(string? header)
    {
        if (header is null)
        {
            return "NIL";
        }

        string[] languages = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return languages.Length == 0 ? "NIL" : "(" + string.Join(' ', languages.Select(ImapFormat.String)) + ")";
    }

    private static string TransferEncoding(MimeEntity entity)
    {
        string? encoding = ImapEnvelope.RawValue(entity.Headers, "Content-Transfer-Encoding");
        return string.IsNullOrWhiteSpace(encoding) ? "7BIT" : encoding.ToUpperInvariant();
    }

    /// <summary>RFC 2231 value encoding: attribute characters stay, every other UTF-8 byte becomes %XX.</summary>
    private static string PercentEncode(string value)
    {
        var result = new StringBuilder();
        foreach (byte character in Encoding.UTF8.GetBytes(value))
        {
            bool plain = char.IsAsciiLetterOrDigit((char)character) || "!#$&+-.^_`|~".Contains((char)character);
            result.Append(plain ? ((char)character).ToString() : "%" + character.ToString("X2"));
        }

        return result.ToString();
    }
}

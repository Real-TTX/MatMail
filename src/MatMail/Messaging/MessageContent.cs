using System.Text.RegularExpressions;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>What the server may and may not change in a message on its way out, and which parts of it are the text the sender wrote.</summary>
public static partial class MessageContent
{
    /// <summary>How much of a plain-text part is looked at for the armour of an inline PGP message.</summary>
    private const int PgpScanLength = 16 * 1024;

    /// <summary>
    /// Signed and encrypted messages must reach the recipient exactly as they were written: any change, even a footer, would break
    /// the signature (or cannot be made at all). That is S/MIME and PGP/MIME, and PGP written straight into the text.
    /// </summary>
    public static bool IsProtected(MimeMessage message) => IsProtected(message.Body) || BodyParts(message).Any(CarriesPgpArmor);

    /// <summary>
    /// The text parts that make up what the sender wrote, in the order of the message: not the attachments, whether they say so
    /// ("Content-Disposition: attachment") or only name a file (a log or report that a script attached as text).
    /// </summary>
    public static IReadOnlyList<TextPart> BodyParts(MimeMessage message)
        => message.BodyParts.OfType<TextPart>().Where(part => !part.IsAttachment && string.IsNullOrEmpty(part.FileName)).ToList();

    /// <summary>
    /// Replaces the text of a part. The transfer encoding it had (7bit, say) may not fit the new characters, so the part is prepared
    /// again: plain ASCII stays as it is, anything else is encoded (quoted-printable or base64) or sent as 8-bit text.
    /// </summary>
    public static void SetText(TextPart part, string text)
    {
        part.Text = text;
        part.ContentTransferEncoding = ContentEncoding.Default;
        part.Prepare(EncodingConstraint.EightBit);
    }

    private static bool IsProtected(MimeEntity? entity) => entity switch
    {
        Multipart multipart when multipart.ContentType.IsMimeType("multipart", "signed") || multipart.ContentType.IsMimeType("multipart", "encrypted") => true,
        Multipart multipart => multipart.Any(IsProtected),
        MimePart part => part.ContentType.IsMimeType("application", "pkcs7-mime") || part.ContentType.IsMimeType("application", "x-pkcs7-mime"),
        _ => false,
    };

    /// <summary>Inline PGP: the armour starts a line (a quoted one begins with "&gt;" and does not count).</summary>
    private static bool CarriesPgpArmor(TextPart part)
    {
        if (!part.IsPlain)
        {
            return false;
        }

        string text = part.Text;
        return PgpArmor().IsMatch(text.Length > PgpScanLength ? text[..PgpScanLength] : text);
    }

    [GeneratedRegex(@"^[ \t]*-----BEGIN PGP (SIGNED )?MESSAGE-----", RegexOptions.Multiline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PgpArmor();
}

using MimeKit;

namespace MatMail.Messaging;

/// <summary>What the server may and may not change in a message on its way out.</summary>
public static class MessageContent
{
    /// <summary>
    /// Signed and encrypted messages must reach the recipient exactly as they were written: any change, even a footer, would break
    /// the signature (or cannot be made at all).
    /// </summary>
    public static bool IsProtected(MimeMessage message) => IsProtected(message.Body);

    private static bool IsProtected(MimeEntity? entity) => entity switch
    {
        Multipart multipart when multipart.ContentType.IsMimeType("multipart", "signed") || multipart.ContentType.IsMimeType("multipart", "encrypted") => true,
        Multipart multipart => multipart.Any(IsProtected),
        MimePart part => part.ContentType.IsMimeType("application", "pkcs7-mime") || part.ContentType.IsMimeType("application", "x-pkcs7-mime"),
        _ => false,
    };
}

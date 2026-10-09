using MimeKit;
using MimeKit.Utils;
using MsgReader.Outlook;

namespace MatMail.Messaging;

/// <summary>A message read from a file: the bytes as RFC 822 (what is stored, shown and downloaded) and the parsed message.</summary>
public sealed record MessageFile(byte[] Raw, MimeMessage Mime, string Format);

/// <summary>
/// Reads a message from a file somebody brought: an <c>.eml</c> (RFC 822) or an Outlook <c>.msg</c>. What the file is, is told by its content,
/// not its name. A <c>.msg</c> is turned into an ordinary message (sender, recipients, subject, date, text, pictures and attachments), so that
/// everything after it – the reader, the download, the import into a folder – has one kind of message to deal with.
/// </summary>
public static class MessageFiles
{
    private static readonly byte[] CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>How deep a message that is an attachment of a message that is an attachment … is followed.</summary>
    private const int MaxDepth = 3;

    /// <exception cref="InvalidDataException">The file is no message (the text says what is wrong with it).</exception>
    public static MessageFile Read(byte[] content)
    {
        if (content.Length == 0)
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (content.AsSpan().StartsWith(CompoundFile))
        {
            MimeMessage converted = FromOutlook(content, depth: 0);
            using var stream = new MemoryStream();
            converted.WriteTo(stream);
            return new MessageFile(stream.ToArray(), converted, "msg");
        }

        MimeMessage mime;
        try
        {
            mime = MimeMessage.Load(new MemoryStream(content));
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            throw new InvalidDataException("The file is not a message.", ex);
        }

        // Any text parses as a message without headers: it is a message when it says who wrote it, to whom, about what or when.
        if (!mime.Headers.Contains(HeaderId.From) && !mime.Headers.Contains(HeaderId.To) && !mime.Headers.Contains(HeaderId.Subject)
            && !mime.Headers.Contains(HeaderId.Date) && !mime.Headers.Contains(HeaderId.MessageId))
        {
            throw new InvalidDataException("The file is not a message.");
        }

        return new MessageFile(content, mime, "eml");
    }

    private static MimeMessage FromOutlook(byte[] content, int depth)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var msg = new Storage.Message(stream);
            return Convert(msg, depth);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The file is not a readable Outlook message.", ex);
        }
    }

    private static MimeMessage Convert(Storage.Message msg, int depth)
    {
        // Outlook keeps appointments, contacts and tasks in the same kind of file.
        string kind = msg.Type.ToString();
        if (kind.StartsWith("Appointment", StringComparison.Ordinal) || kind.StartsWith("Task", StringComparison.Ordinal) || kind is "Contact" or "Journal" or "StickyNote")
        {
            throw new InvalidDataException("The file is an Outlook item, but not a message.");
        }

        var mime = new MimeMessage { Subject = msg.Subject ?? string.Empty };
        mime.Headers.Remove(HeaderId.MessageId);   // a new message makes up an id and a date: what the file does not have is not invented
        mime.Headers.Remove(HeaderId.Date);
        if ((msg.SentOn ?? msg.ReceivedOn) is { } date)
        {
            mime.Date = date;
        }

        string? senderAddress = msg.Sender?.Email ?? msg.SenderRepresenting?.Email;
        string? senderName = msg.Sender?.DisplayName ?? msg.SenderRepresenting?.DisplayName;
        if (!string.IsNullOrWhiteSpace(senderAddress) || !string.IsNullOrWhiteSpace(senderName))
        {
            mime.From.Add(Address(senderName, senderAddress));
        }

        foreach (Storage.Recipient recipient in msg.Recipients ?? [])
        {
            InternetAddressList? list = recipient.Type switch
            {
                RecipientType.To => mime.To,
                RecipientType.Cc => mime.Cc,
                RecipientType.Bcc => mime.Bcc,
                _ => null,
            };
            list?.Add(Address(recipient.DisplayName, recipient.Email));
        }

        CopyTransportHeaders(msg.TransportMessageHeaders, mime);
        if (mime.MessageId is null && msg.Id is { Length: > 0 } id && MimeUtils.EnumerateReferences(id).FirstOrDefault() is { } fromFile)
        {
            mime.MessageId = fromFile;
        }

        var body = new BodyBuilder { HtmlBody = msg.BodyHtml, TextBody = msg.BodyText };
        foreach (object part in msg.Attachments ?? [])
        {
            switch (part)
            {
                case Storage.Attachment file when file.Data is { Length: > 0 }:
                    AddAttachment(body, file);
                    break;
                case Storage.Message nested when depth < MaxDepth:
                    body.Attachments.Add(new MessagePart("rfc822") { Message = Convert(nested, depth + 1) });
                    break;
            }
        }

        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static void AddAttachment(BodyBuilder body, Storage.Attachment file)
    {
        string name = string.IsNullOrWhiteSpace(file.FileName) ? "attachment" : file.FileName;
        ContentType type = ContentType.TryParse(file.MimeType ?? string.Empty, out ContentType? parsed) ? parsed : new ContentType("application", "octet-stream");
        if (file.IsInline && !string.IsNullOrWhiteSpace(file.ContentId))
        {
            MimeEntity picture = body.LinkedResources.Add(name, file.Data, type);
            picture.ContentId = file.ContentId;
        }
        else
        {
            body.Attachments.Add(name, file.Data, type);
        }
    }

    /// <summary>The ids that make a reply find its conversation, from the headers that travelled with the message.</summary>
    private static void CopyTransportHeaders(string? transport, MimeMessage mime)
    {
        if (string.IsNullOrWhiteSpace(transport))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(transport.TrimEnd() + "\r\n\r\n"));
            HeaderList headers = HeaderList.Load(stream);
            if (headers[HeaderId.MessageId] is { Length: > 0 } id && MimeUtils.EnumerateReferences(id).FirstOrDefault() is { } messageId)
            {
                mime.MessageId = messageId;
            }

            if (headers[HeaderId.InReplyTo] is { Length: > 0 } inReplyTo)
            {
                mime.InReplyTo = MimeUtils.EnumerateReferences(inReplyTo).FirstOrDefault();
            }

            if (headers[HeaderId.References] is { Length: > 0 } references)
            {
                foreach (string reference in MimeUtils.EnumerateReferences(references))
                {
                    mime.References.Add(reference);
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            // The headers of a file are no reason to refuse it: it is shown without them.
        }
    }

    /// <summary>An address as the file has it; Exchange keeps internal senders as a path (/O=…/CN=…), which is no address: only the name is shown then.</summary>
    private static MailboxAddress Address(string? name, string? address)
        => new(name ?? string.Empty, !string.IsNullOrWhiteSpace(address) && address.Contains('@') && !address.Contains('/') ? address.Trim() : "unknown@unknown.invalid");
}

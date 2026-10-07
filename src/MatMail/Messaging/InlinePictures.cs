using System.Text.RegularExpressions;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>
/// Pictures in signatures, footers and templates are embedded as data: addresses while they are edited. Mail programs do not show
/// those; on the way out they become inline parts of the message that the HTML refers to by cid:.
/// </summary>
public static partial class InlinePictures
{
    /// <summary>Replaces the embedded pictures of the HTML by cid: references and collects the pictures they stand for.</summary>
    public static string Reference(string html, List<MimePart> pictures)
        => DataPictureInHtml().Replace(html, match =>
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(match.Groups[2].Value);
            }
            catch (FormatException)
            {
                return match.Value;
            }

            string[] type = match.Groups[1].Value.Split('/');
            var picture = new MimePart(type[0], type[1])
            {
                Content = new MimeContent(new MemoryStream(bytes)),
                ContentTransferEncoding = ContentEncoding.Base64,
                ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            };
            pictures.Add(picture);
            return "src=\"cid:" + picture.ContentId + "\"";
        });

    /// <summary>Puts the pictures next to the HTML part, in a multipart/related that holds both.</summary>
    public static void Attach(MimeMessage message, TextPart html, IReadOnlyList<MimePart> pictures)
    {
        if (pictures.Count == 0)
        {
            return;
        }

        (Multipart? parent, int index) = FindParent(message.Body, html);
        if (parent is not null && parent.ContentType.IsMimeType("multipart", "related"))
        {
            foreach (MimePart picture in pictures)
            {
                parent.Add(picture);
            }

            return;
        }

        var related = new Multipart("related") { html };
        foreach (MimePart picture in pictures)
        {
            related.Add(picture);
        }

        if (parent is not null)
        {
            parent[index] = related;
        }
        else if (ReferenceEquals(message.Body, html))
        {
            message.Body = related;
        }
    }

    /// <summary>The multipart that holds the entity, and the entity's place in it; (null, -1) when it is the message body itself.</summary>
    public static (Multipart? Parent, int Index) FindParent(MimeEntity? root, MimeEntity target)
    {
        if (root is not Multipart multipart)
        {
            return (null, -1);
        }

        for (int i = 0; i < multipart.Count; i++)
        {
            if (ReferenceEquals(multipart[i], target))
            {
                return (multipart, i);
            }

            (Multipart? parent, int index) = FindParent(multipart[i], target);
            if (parent is not null)
            {
                return (parent, index);
            }
        }

        return (null, -1);
    }

    [GeneratedRegex(@"src=""data:(image/(?:png|jpeg|gif|webp));base64,([A-Za-z0-9+/=\s]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DataPictureInHtml();
}

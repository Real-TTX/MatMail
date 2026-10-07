using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;
using MimeKit;
using MimeKit.Text;

namespace MatMail.Messaging;

/// <summary>An attachment of a message as the web client lists it.</summary>
public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size);

/// <summary>The body of a message made safe to show: sanitised HTML, and whether it wants to load things from the internet.</summary>
public sealed record RenderedBody(string Html, bool HasRemoteContent, bool WasPlainText);

/// <summary>
/// Turns a received message into HTML the browser may show. Everything active is removed (scripts, forms, frames, event handlers,
/// styles that load things); links open in a new tab; <c>cid:</c> images point at the message's own inline parts; images from the
/// internet are blocked until the reader asks for them (they tell the sender that and when a mail was opened).
/// </summary>
public sealed partial class MailBodyRenderer
{
    public const string BlankImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private static readonly string[] ExtraTags =
    {
        "center", "font", "u", "s", "strike", "big", "small", "abbr", "address", "cite", "mark", "figure", "figcaption", "details", "summary",
        "col", "colgroup", "caption", "thead", "tbody", "tfoot",
    };

    private static readonly string[] ExtraAttributes =
    {
        "bgcolor", "background", "align", "valign", "width", "height", "border", "cellpadding", "cellspacing", "color", "face", "size",
        "colspan", "rowspan", "dir", "lang", "nowrap", "hspace", "vspace", "bordercolor", "style", "class",
    };

    private static readonly string[] ExtraCss =
    {
        "background", "background-color", "border", "border-top", "border-bottom", "border-left", "border-right", "border-color", "border-style",
        "border-width", "border-radius", "border-collapse", "border-spacing", "color", "display", "float", "font", "font-family", "font-size",
        "font-style", "font-weight", "height", "width", "max-width", "min-width", "max-height", "min-height", "line-height", "letter-spacing",
        "margin", "margin-top", "margin-bottom", "margin-left", "margin-right", "padding", "padding-top", "padding-bottom", "padding-left",
        "padding-right", "text-align", "text-decoration", "text-indent", "text-transform", "vertical-align", "white-space", "word-break",
        "word-wrap", "overflow-wrap", "list-style", "list-style-type", "table-layout", "clear", "opacity", "direction",
    };

    /// <summary>The message's visible text as safe HTML.</summary>
    public RenderedBody Render(MimeMessage message, long messageId, bool allowRemoteImages)
    {
        string? html = message.HtmlBody;
        bool plain = false;
        if (string.IsNullOrWhiteSpace(html))
        {
            plain = true;
            var converter = new TextToHtml { Header = string.Empty, Footer = string.Empty, OutputHtmlFragment = true };
            html = "<div style=\"white-space:pre-wrap;word-wrap:break-word\">" + converter.Convert(message.TextBody ?? string.Empty) + "</div>";
        }

        bool remoteFound = false;
        HtmlSanitizer sanitizer = BuildSanitizer(messageId, allowRemoteImages, () => remoteFound = true);
        string clean = sanitizer.Sanitize(html);
        return new RenderedBody(clean, remoteFound, plain);
    }

    /// <summary>The attachments a reader can download (inline images the HTML shows are not listed).</summary>
    public IReadOnlyList<AttachmentInfo> GetAttachments(MimeMessage message)
    {
        var result = new List<AttachmentInfo>();
        foreach (MimeEntity entity in AttachmentEntities(message))
        {
            int index = result.Count;
            result.Add(new AttachmentInfo(index, FileNameOf(entity, index + 1), entity.ContentType.MimeType, SizeOf(entity)));
        }

        return result;
    }

    /// <summary>The attachment with this index (as numbered by <see cref="GetAttachments"/>).</summary>
    public MimeEntity? FindAttachment(MimeMessage message, int index) => AttachmentEntities(message).Skip(index).FirstOrDefault();

    /// <summary>The part with this Content-ID (for <c>cid:</c> images).</summary>
    public MimePart? FindByContentId(MimeMessage message, string contentId)
    {
        string wanted = contentId.Trim('<', '>');
        return message.BodyParts.OfType<MimePart>().FirstOrDefault(p => string.Equals(p.ContentId?.Trim('<', '>'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static string FileNameOf(MimeEntity entity, int number)
    {
        string? name = (entity as MimePart)?.FileName ?? entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return entity is MessagePart ? $"message-{number}.eml" : $"attachment-{number}";
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static IEnumerable<MimeEntity> AttachmentEntities(MimeMessage message)
    {
        foreach (MimeEntity entity in message.BodyParts)
        {
            if (entity is Multipart)
            {
                continue;
            }

            bool isDisposedAsAttachment = entity.ContentDisposition?.IsAttachment ?? false;

            // The text of the mail itself.
            bool isBodyText = entity is TextPart text && !isDisposedAsAttachment && string.IsNullOrEmpty(text.FileName) && (text.IsHtml || text.IsPlain);
            if (isBodyText)
            {
                continue;
            }

            // Images the HTML embeds by Content-ID.
            bool isInlineImage = entity.ContentType.MediaType == "image" && !string.IsNullOrEmpty(entity.ContentId) && !isDisposedAsAttachment;
            if (isInlineImage)
            {
                continue;
            }

            yield return entity;
        }
    }

    private static long SizeOf(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        switch (entity)
        {
            case MimePart { Content: not null } part:
                part.Content.DecodeTo(stream);
                break;
            case MessagePart { Message: not null } messagePart:
                messagePart.Message.WriteTo(stream);
                break;
        }

        return stream.Length;
    }

    private static HtmlSanitizer BuildSanitizer(long messageId, bool allowRemoteImages, Action remoteFound)
    {
        var sanitizer = new HtmlSanitizer();
        Array.ForEach(ExtraTags, tag => sanitizer.AllowedTags.Add(tag));
        Array.ForEach(ExtraAttributes, attribute => sanitizer.AllowedAttributes.Add(attribute));
        Array.ForEach(ExtraCss, property => sanitizer.AllowedCssProperties.Add(property));
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowDataAttributes = false;

        // cid: images point at the message's own inline parts.
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = $"/api/mail/messages/{messageId}/cid/{WebUtility.UrlEncode(e.OriginalUrl[4..])}";
            }
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement element)
            {
                return;
            }

            switch (element.TagName.ToLowerInvariant())
            {
                case "a":
                    PrepareLink(element);
                    break;
                case "img":
                    PrepareImage(element, allowRemoteImages, remoteFound);
                    break;
            }

            // Things that load pictures from the internet through attributes and styles.
            if (!allowRemoteImages)
            {
                BlockRemoteBackgrounds(element, remoteFound);
            }
        };

        return sanitizer;
    }

    private static void PrepareLink(AngleSharp.Dom.IElement link)
    {
        string? href = link.GetAttribute("href");
        if (href is not null && href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            link.RemoveAttribute("href");
        }

        link.SetAttribute("target", "_blank");
        link.SetAttribute("rel", "noopener noreferrer nofollow");
    }

    private static void PrepareImage(AngleSharp.Dom.IElement image, bool allowRemoteImages, Action remoteFound)
    {
        image.RemoveAttribute("srcset");
        string? src = image.GetAttribute("src");
        bool remote = src is not null && (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//", StringComparison.Ordinal));
        if (remote && !allowRemoteImages)
        {
            remoteFound();
            image.SetAttribute("data-blocked", "1");
            image.SetAttribute("src", BlankImage);
        }
        else if (string.IsNullOrEmpty(src))
        {
            image.SetAttribute("src", BlankImage);
        }
    }

    private static void BlockRemoteBackgrounds(AngleSharp.Dom.IElement element, Action remoteFound)
    {
        string? background = element.GetAttribute("background");
        if (background is not null && RemoteUrl().IsMatch(background))
        {
            element.RemoveAttribute("background");
            remoteFound();
        }

        string? style = element.GetAttribute("style");
        if (style is not null && CssRemoteUrl().IsMatch(style))
        {
            element.SetAttribute("style", CssRemoteUrl().Replace(style, "none"));
            remoteFound();
        }
    }

    [GeneratedRegex(@"^\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"url\(\s*['""]?\s*(https?:)?//[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssRemoteUrl();
}

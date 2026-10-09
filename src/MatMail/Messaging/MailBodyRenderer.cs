using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using MimeKit;
using MimeKit.Text;

namespace MatMail.Messaging;

/// <summary>An attachment of a message as the web client lists it.</summary>
public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size);

/// <summary>
/// The body of a message made safe to show: sanitised HTML, whether it wants to load things from the internet, and whether the
/// sender chose its colours (then it is shown on a light page even in the dark theme, because its text colours assume one).
/// </summary>
public sealed record RenderedBody(string Html, bool HasRemoteContent, bool WasPlainText, bool HasOwnColours = false);

/// <summary>One line of the header block of a printed message ("From", "Anna &lt;anna@…&gt;").</summary>
public sealed record PrintRow(string Label, string Value);

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
        => Render(message, $"/api/mail/messages/{messageId}", allowRemoteImages);

    /// <param name="urlBase">The address the parts of the message are served below (inline pictures at <c>{urlBase}/cid/…</c>).</param>
    public RenderedBody Render(MimeMessage message, string urlBase, bool allowRemoteImages)
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
        HtmlSanitizer sanitizer = BuildSanitizer(urlBase, allowRemoteImages, () => remoteFound = true);
        string clean = sanitizer.Sanitize(plain ? html : MoveBodyLookIntoContent(html));
        return new RenderedBody(clean, remoteFound, plain, !plain && ColourChoice().IsMatch(clean));
    }

    /// <summary>
    /// The complete page a sandboxed iframe shows. The classes tell the style sheet (and the page that embeds it) how the mail wants
    /// to be treated: plain text, HTML without colours of its own, or HTML with them.
    /// </summary>
    public static string BuildDocument(RenderedBody body)
    {
        string kind = body.WasPlainText ? "mm-plain" : body.HasOwnColours ? "mm-styled" : "mm-unstyled";
        return "<!doctype html><html class=\"" + kind + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base target=\"_blank\"><style>" + DocumentCss + "</style></head><body><div id=\"mm-body\">" + body.Html + "</div></body></html>";
    }

    /// <summary>
    /// The message as a page of its own for printing: the header (subject, people, date, attachments) above the body, light whatever the
    /// theme of the reader is. A small script (the caller's CSP lets exactly that one run, by its nonce) opens the print dialog and
    /// gives the screen two buttons; the mail itself cannot run anything. Phones print this page like any other, from the browser.
    /// </summary>
    public static string BuildPrintDocument(RenderedBody body, string subject, IReadOnlyList<PrintRow> rows, string printText, string closeText, string language, string nonce)
    {
        string kind = body.WasPlainText ? "mm-plain" : body.HasOwnColours ? "mm-styled" : "mm-unstyled";
        var header = new System.Text.StringBuilder();
        foreach (PrintRow row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            header.Append("<tr><th>").Append(WebUtility.HtmlEncode(row.Label)).Append("</th><td>").Append(WebUtility.HtmlEncode(row.Value)).Append("</td></tr>");
        }

        string title = WebUtility.HtmlEncode(subject);
        return "<!doctype html><html class=\"" + kind + "\" lang=\"" + WebUtility.HtmlEncode(language) + "\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + title + "</title>"
            + "<base target=\"_blank\"><style>" + DocumentCss + PrintCss + "</style></head><body>"
            + "<div class=\"mm-print-bar\"><button type=\"button\" id=\"mm-print\">" + WebUtility.HtmlEncode(printText) + "</button>"
            + "<button type=\"button\" id=\"mm-close\">" + WebUtility.HtmlEncode(closeText) + "</button></div>"
            + "<header class=\"mm-print-head\"><h1>" + title + "</h1><table>" + header + "</table></header>"
            + "<div id=\"mm-body\">" + body.Html + "</div>"
            + "<script nonce=\"" + WebUtility.HtmlEncode(nonce) + "\">" + PrintScript + "</script></body></html>";
    }

    private const string PrintScript =
        "(function(){"
        + "function leave(){if(window.opener){window.close();}else if(history.length>1){history.back();}else{window.close();}}"
        + "document.getElementById('mm-print').addEventListener('click',function(){window.print();});"
        + "document.getElementById('mm-close').addEventListener('click',leave);"
        + "window.addEventListener('load',function(){setTimeout(function(){window.print();},300);});"
        + "})();";

    private const string PrintCss =
        "body{background:#fff}"
        + ".mm-print-bar{position:sticky;top:0;z-index:2;display:flex;gap:8px;padding:8px 18px;background:#f1f3f4;border-bottom:1px solid #dadce0}"
        + ".mm-print-bar button{font:inherit;padding:6px 16px;border:1px solid #c3cadb;border-radius:6px;background:#fff;color:#202124;cursor:pointer}"
        + ".mm-print-bar button:first-child{background:#1a73e8;border-color:#1a73e8;color:#fff}"
        + ".mm-print-head{padding:16px 18px 6px;border-bottom:1px solid #dadce0}"
        + ".mm-print-head h1{margin:0 0 8px;font-size:20px;line-height:1.3;font-weight:600}"
        + ".mm-print-head table{border-collapse:collapse;font-size:13px;margin-bottom:8px}"
        + ".mm-print-head th{padding:1px 14px 1px 0;color:#5f6368;font-weight:600;text-align:left;vertical-align:top;white-space:nowrap}"
        + ".mm-print-head td{padding:1px 0;overflow-wrap:anywhere}"
        + "@page{margin:14mm}"
        + "@media print{.mm-print-bar{display:none}.mm-print-head{padding:0 0 6px}#mm-body{padding:10px 0}a{color:inherit}}";

    private const string DocumentCss =
        "html{background:transparent;touch-action:pan-x pan-y}"
        + "body{margin:0;padding:0;font:14px/1.55 system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif;color:#202124;background:#fff;overflow-wrap:break-word}"
        + "#mm-body{padding:14px 18px}"
        + "img{max-width:100%;height:auto}"
        + "pre{white-space:pre-wrap}"
        + "blockquote{margin:.6em 0 .6em .4em;padding-left:.8em;border-left:3px solid #dadce0;color:#5f6368}"
        + "a{color:#1a73e8}"
        + "img[data-blocked]{background:repeating-linear-gradient(45deg,#f1f3f4,#f1f3f4 6px,#e8eaed 6px,#e8eaed 12px);min-width:24px;min-height:24px}"
        + ".mm-quote-toggle{display:inline-block;margin:6px 0;padding:0 9px;height:16px;line-height:14px;border:0;border-radius:8px;background:#e8eaed;color:#5f6368;cursor:pointer;font:700 12px/16px system-ui,sans-serif}"
        + ".mm-quote-toggle:hover{background:#dadce0}"
        + ".mm-quote[hidden]{display:none}"
        // Mails without colours of their own follow the dark theme; the ones with colours stay on their light page.
        + "html.mm-dark.mm-plain body,html.mm-dark.mm-unstyled body{background:transparent;color:#e8eaed}"
        + "html.mm-dark.mm-plain a,html.mm-dark.mm-unstyled a{color:#8ab4f8}"
        + "html.mm-dark.mm-plain blockquote,html.mm-dark.mm-unstyled blockquote{color:#9aa0a6;border-left-color:#5f6368}"
        + "html.mm-dark.mm-plain img[data-blocked],html.mm-dark.mm-unstyled img[data-blocked]{background:repeating-linear-gradient(45deg,#2a2d32,#2a2d32 6px,#33373d 6px,#33373d 12px)}"
        + "html.mm-dark.mm-plain .mm-quote-toggle,html.mm-dark.mm-unstyled .mm-quote-toggle{background:#3c4043;color:#bdc1c6}";

    /// <summary>
    /// Newsletters put their page colour and text colour on the body element, which the sanitiser removes together with the element.
    /// Their look moves to a div that holds the content instead.
    /// </summary>
    private static string MoveBodyLookIntoContent(string html)
    {
        if (html.IndexOf("<body", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html;
        }

        AngleSharp.Html.Dom.IHtmlDocument document = new HtmlParser().ParseDocument(html);
        AngleSharp.Html.Dom.IHtmlElement? body = document.Body;
        if (body is null)
        {
            return html;
        }

        // The style sheets of the head (newsletters style their text through classes) belong to the content as well.
        string sheets = string.Concat(document.Head?.QuerySelectorAll("style").Select(s => s.OuterHtml) ?? Enumerable.Empty<string>());

        var style = new System.Text.StringBuilder();
        string? background = body.GetAttribute("bgcolor");
        if (!string.IsNullOrWhiteSpace(background))
        {
            style.Append("background-color:").Append(background).Append(';');
        }

        string? text = body.GetAttribute("text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            style.Append("color:").Append(text).Append(';');
        }

        string? own = body.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(own))
        {
            style.Append(own);
        }

        return sheets + (style.Length == 0 ? body.InnerHtml : "<div style=\"" + WebUtility.HtmlEncode(style.ToString()) + "\">" + body.InnerHtml + "</div>");
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

    private static HtmlSanitizer BuildSanitizer(string urlBase, bool allowRemoteImages, Action remoteFound)
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

        // Style sheets of the sender (newsletters and Word mails style their text with classes). The sanitiser cleans the CSS: no
        // @import, no @font-face, only known properties; @media stays, because many mails switch to a narrow layout with it.
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowedCssProperties.Remove("position");
        sanitizer.AllowedCssProperties.Remove("z-index");

        sanitizer.FilterUrl += (_, e) =>
        {
            // cid: images point at the message's own inline parts.
            if (e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = $"{urlBase}/cid/{WebUtility.UrlEncode(e.OriginalUrl[4..])}";
            }
            else if (!allowRemoteImages && e.Tag?.TagName == "STYLE" && RemoteUrl().IsMatch(e.OriginalUrl))
            {
                // url() in a style sheet loads from the internet, just like an image.
                e.SanitizedUrl = null;
                remoteFound();
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

        // The reader sees where a link really leads before clicking (the text of a link can say anything).
        if (!string.IsNullOrEmpty(href) && !link.HasAttribute("title") && !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            link.SetAttribute("title", href.Length > 300 ? href[..300] : href);
        }
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

    /// <summary>Colours the sender chose: a background, a text colour, a font colour.</summary>
    [GeneratedRegex(@"\b(bgcolor|background|color)\s*[=:]", RegexOptions.IgnoreCase)]
    private static partial Regex ColourChoice();

    [GeneratedRegex(@"^\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"url\(\s*['""]?\s*(https?:)?//[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssRemoteUrl();
}

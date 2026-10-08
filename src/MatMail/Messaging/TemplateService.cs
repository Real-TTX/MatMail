using System.Net;
using System.Text.RegularExpressions;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>
/// Mail templates: the HTML frame a message is put into on its way out. A template is chosen by rule: who sends (user, mailbox,
/// tenant), where the message comes from (web client, mail program, smart host - optionally one smart-host rule) and whether it has an
/// HTML version at all. The main use is the plain-text message of a device, script or simple SMTP client, which leaves as a proper HTML
/// mail with the plain-text version kept next to it. Signature and footers are added afterwards.
/// Signed and encrypted messages are left alone; so are reports (bounces and the like).
/// </summary>
public sealed partial class TemplateService
{
    /// <summary>
    /// The longest text that is put into a template (bytes of the part as it came in). A message beyond it goes out as it is: turning
    /// 50 MB of text into HTML would cost several times that in memory.
    /// </summary>
    private const long MaxTextLength = 4_000_000;

    private readonly MatMailDbContext _db;

    public TemplateService(MatMailDbContext db) => _db = db;

    /// <summary>The templates that fit the sender and the way the message came in, the most specific first.</summary>
    public async Task<IReadOnlyList<MailTemplate>> FindAsync(
        SubmissionSource source, long tenantId, long? mailboxId, long? userId, long? relayRuleId, CancellationToken cancel = default)
    {
        bool web = source == SubmissionSource.Web;
        bool program = source == SubmissionSource.MailProgram;
        bool smartHost = source == SubmissionSource.SmartHost;

        List<MailTemplate> fitting = await _db.MailTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.IsActive
                        && (t.Scope == AppliesTo.Tenant
                            || (t.Scope == AppliesTo.Mailbox && t.MailboxId == mailboxId)
                            || (t.Scope == AppliesTo.User && t.UserId == userId))
                        && (t.RelayRuleId == null || (smartHost && t.RelayRuleId == relayRuleId))
                        && ((web && t.ForWebClient) || (program && t.ForMailPrograms) || (smartHost && t.ForSmartHost)))
            .ToListAsync(cancel);

        return fitting
            .OrderBy(t => t.Scope == AppliesTo.User ? 0 : t.Scope == AppliesTo.Mailbox ? 1 : 2)
            .ThenBy(t => t.RelayRuleId is null ? 1 : 0)
            .ThenBy(t => t.Priority)
            .ThenBy(t => t.Id)
            .ToList();
    }

    /// <summary>Puts the message into the first template that fits; returns that template (null: the message was left as it is).</summary>
    public async Task<MailTemplate?> ApplyAsync(
        MimeMessage message, SubmissionSource source, long tenantId, long? mailboxId, long? userId, long? relayRuleId, SignatureContext context,
        CancellationToken cancel = default)
    {
        if (MessageContent.IsProtected(message) || message.Body is { } body && body.ContentType.IsMimeType("multipart", "report"))
        {
            return null;
        }

        // What the sender wrote, not what a script attached as text. A message that is cut into several texts (pictures in between) is
        // not framed: where would the frame begin?
        IReadOnlyList<TextPart> bodies = MessageContent.BodyParts(message);
        TextPart[] plains = bodies.Where(p => p.IsPlain).ToArray();
        TextPart[] htmls = bodies.Where(p => p.IsHtml).ToArray();
        if (plains.Length + htmls.Length == 0 || plains.Length > 1 || htmls.Length > 1)
        {
            return null;
        }

        TextPart? plain = plains.FirstOrDefault();
        TextPart? html = htmls.FirstOrDefault();
        if (Size(plain) > MaxTextLength || Size(html) > MaxTextLength)
        {
            return null;
        }

        foreach (MailTemplate template in await FindAsync(source, tenantId, mailboxId, userId, relayRuleId, cancel))
        {
            bool fits = template.Mode == TemplateMode.AllMessages || html is null;
            if (!fits || !HasPlaceForBody(template.Html))
            {
                continue;
            }

            try
            {
                Put(message, template, plain, html, context);
            }
            catch (RegexMatchTimeoutException)
            {
                // A body that the patterns cannot digest in reasonable time goes out as it is.
                continue;
            }

            return template;
        }

        return null;
    }

    /// <summary>Whether the template says where the message goes; a template without {{Body}} would swallow it.</summary>
    public static bool HasPlaceForBody(string templateHtml) => BodyPlaceholder().IsMatch(templateHtml);

    private static void Put(MimeMessage message, MailTemplate template, TextPart? plain, TextPart? html, SignatureContext context)
    {
        var pictures = new List<MimePart>();
        string frame = InlinePictures.Reference(Frame(template, context), pictures);

        if (html is not null)
        {
            MessageContent.SetText(html, WrapDocument(html.Text, frame));
            InlinePictures.Attach(message, html, pictures);
            return;
        }

        string document = "<!DOCTYPE html>\r\n<html><head><meta charset=\"utf-8\"></head><body>" + Insert(frame, TextToHtml(plain!.Text)) + "</body></html>";
        var created = new TextPart("html");
        MessageContent.SetText(created, document);
        AddAlternative(message, plain, created);
        InlinePictures.Attach(message, created, pictures);
    }

    /// <summary>The template with the placeholders of the sender filled in; {{Body}} stays for the message.</summary>
    private static string Frame(MailTemplate template, SignatureContext context)
    {
        string frame = SignatureService.Render(template.Html, context, html: true);

        // The editor wraps lines in paragraphs; a message (with paragraphs of its own) does not belong inside one.
        return BodyInParagraph().Replace(frame, match => $"<div{match.Groups[1].Value}>{{{{Body}}}}</div>");
    }

    private static long Size(TextPart? part) => part?.Content?.Stream?.Length ?? 0;

    /// <summary>The message goes where the first {{Body}} stands; a second one is left empty rather than showing as text.</summary>
    private static string Insert(string frame, string bodyHtml)
    {
        bool first = true;
        return BodyPlaceholder().Replace(frame, _ =>
        {
            if (!first)
            {
                return string.Empty;
            }

            first = false;
            return bodyHtml;
        });
    }

    /// <summary>The message HTML with its body (what is inside &lt;body&gt;) put into the template; the head and the rest stay.</summary>
    private static string WrapDocument(string messageHtml, string frame)
    {
        int bodyTag = IndexOfBodyTag(messageHtml);
        if (bodyTag >= 0)
        {
            int open = messageHtml.IndexOf('>', bodyTag);
            int close = messageHtml.LastIndexOf("</body", StringComparison.OrdinalIgnoreCase);
            if (open >= 0 && close > open)
            {
                return messageHtml[..(open + 1)] + Insert(frame, messageHtml[(open + 1)..close]) + messageHtml[close..];
            }
        }

        return Insert(frame, messageHtml);
    }

    /// <summary>Where "&lt;body" starts as a tag of its own (not "&lt;bodyguard"); -1 when there is none.</summary>
    private static int IndexOfBodyTag(string html)
    {
        for (int from = 0; ;)
        {
            int index = html.IndexOf("<body", from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            int next = index + "<body".Length;
            if (next >= html.Length || html[next] is '>' or '/' || char.IsWhiteSpace(html[next]))
            {
                return index;
            }

            from = next;
        }
    }

    /// <summary>Plain text as HTML: escaped, line breaks kept, web addresses made clickable.</summary>
    private static string TextToHtml(string text)
    {
        string encoded = WebUtility.HtmlEncode(text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd());
        string linked = Url().Replace(encoded, match =>
        {
            string url = match.Value.TrimEnd('.', ',', ':', '!', '?', ')', ']');
            return $"<a href=\"{url}\">{url}</a>{match.Value[url.Length..]}";
        });
        return linked.Replace("\n", "<br>\n");
    }

    /// <summary>Adds the HTML version next to the plain text one, wherever that sits in the message.</summary>
    private static void AddAlternative(MimeMessage message, TextPart plain, TextPart html)
    {
        (Multipart? parent, int index) = InlinePictures.FindParent(message.Body, plain);
        if (parent is null)
        {
            message.Body = new MultipartAlternative { plain, html };
        }
        else if (parent.ContentType.IsMimeType("multipart", "alternative"))
        {
            parent.Insert(index + 1, html);
        }
        else
        {
            parent[index] = new MultipartAlternative { plain, html };
        }
    }

    [GeneratedRegex(@"\{\{\s*Body\s*\}\}", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BodyPlaceholder();

    [GeneratedRegex(@"<p(\s[^>]*)?>\s*\{\{\s*Body\s*\}\}\s*</p>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BodyInParagraph();

    [GeneratedRegex(@"\bhttps?://(?:(?!&(?:lt|gt|quot|#39);)[^\s<>""])+", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Url();
}

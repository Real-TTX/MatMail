using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Messaging;

/// <summary>The values a signature template can use.</summary>
public sealed record SignatureContext(string DisplayName, string Email, string? JobTitle, string? Phone, string Tenant)
{
    public string? Salutation { get; init; }
    public string? Title { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Department { get; init; }
    public string? Mobile { get; init; }
    public string? Fax { get; init; }

    /// <summary>The company's web address (branding of the tenant).</summary>
    public string? Website { get; init; }

    /// <summary>The first name: the one entered, otherwise everything of the display name but its last word.</summary>
    public string FirstNameOrDerived => !string.IsNullOrWhiteSpace(FirstName) ? FirstName : SplitName().First;

    /// <summary>The last name: the one entered, otherwise the last word of the display name.</summary>
    public string LastNameOrDerived => !string.IsNullOrWhiteSpace(LastName) ? LastName : SplitName().Last;

    /// <summary>Title and name as one would write them: "Dr. Max Mustermann".</summary>
    public string FullName => string.Join(' ', new[] { Title, DisplayName }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private (string First, string Last) SplitName()
    {
        string name = DisplayName.Trim();
        int space = name.LastIndexOf(' ');
        return space < 0 ? (name, string.Empty) : (name[..space].Trim(), name[(space + 1)..].Trim());
    }

    /// <summary>The values of a user (null: only the sender's address and name are known) in a tenant.</summary>
    public static SignatureContext For(User? user, string fallbackName, string email, string tenant, string? website = null) => new(
        string.IsNullOrWhiteSpace(user?.DisplayName) ? fallbackName : user.DisplayName, email, user?.JobTitle, user?.Phone, tenant)
    {
        Salutation = user?.Salutation,
        Title = user?.Title,
        FirstName = user?.FirstName,
        LastName = user?.LastName,
        Department = user?.Department,
        Mobile = user?.Mobile,
        Fax = user?.Fax,
        Website = website,
    };
}

/// <summary>
/// Signatures and footers. A <see cref="SignatureKind.Signature"/> is offered by the mail client and, when it is set to be added on
/// the server, also appended to messages of mail programs that were written without one. A <see cref="SignatureKind.Footer"/> is
/// appended to every outgoing message in its scope, whatever client sent it (web, Outlook, a device through the smart host).
/// Pictures in them travel as inline parts of the message.
/// </summary>
public sealed partial class SignatureService
{
    /// <summary>The placeholders the editors offer, comma separated, in the order of the toolbar.</summary>
    public const string Placeholders = "FullName,Salutation,Title,FirstName,LastName,DisplayName,JobTitle,Department,Email,Phone,Mobile,Fax,Tenant,Website";

    private static readonly string[] PlaceholderNames =
    {
        "displayname", "name", "fullname", "firstname", "lastname", "salutation", "title", "email", "jobtitle", "position", "department",
        "phone", "mobile", "fax", "website", "tenant", "company",
    };

    /// <summary>Elements that start a new line of their own; a line break (&lt;br&gt;) ends a line inside one.</summary>
    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "ul", "ol", "table", "tbody", "thead", "tfoot", "tr", "td", "th", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "address",
    };

    /// <summary>Block elements that disappear when their last line was left out (cells and rows stay: a table keeps its shape).</summary>
    private static readonly HashSet<string> RemovableBlocks = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "ul", "ol", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer", "address",
    };

    /// <summary>Elements that make an inline wrapper more than a piece of a line: it holds lines of its own.</summary>
    private const string LineStructure = "br, p, div, li, ul, ol, table, blockquote, h1, h2, h3, h4, h5, h6, pre, section, article, header, footer, address";

    private readonly MatMailDbContext _db;

    public SignatureService(MatMailDbContext db) => _db = db;

    /// <summary>
    /// Replaces {{Placeholders}} (values are HTML-encoded for HTML templates). A line in which every placeholder is empty is left out
    /// together with its label: "Phone: {{Phone}}" does not show for somebody without a phone number.
    /// </summary>
    public static string Render(string template, SignatureContext context, bool html)
    {
        string withoutEmptyLines = html ? DropEmptyHtmlLines(template, context) : DropEmptyTextLines(template, context);
        string rendered = Placeholder().Replace(withoutEmptyLines, match =>
        {
            string? value = ValueOf(match.Groups[1].Value, context);
            return value is null ? match.Value : html ? WebUtility.HtmlEncode(value) : value;
        });

        // Signatures are written by administrators but shown inside the mail client of every user (it inserts them into the message
        // being written), so they get the same treatment as received mail: no scripts, event handlers or script URLs.
        return html ? Sanitize(rendered) : rendered;
    }

    /// <summary>The value of a placeholder; null when there is no placeholder of that name (it stays as it is written).</summary>
    private static string? ValueOf(string name, SignatureContext context) => name.ToLowerInvariant() switch
    {
        "displayname" or "name" => context.DisplayName,
        "fullname" => context.FullName,
        "firstname" => context.FirstNameOrDerived,
        "lastname" => context.LastNameOrDerived,
        "salutation" => context.Salutation ?? string.Empty,
        "title" => context.Title ?? string.Empty,
        "email" => context.Email,
        "jobtitle" or "position" => context.JobTitle ?? string.Empty,
        "department" => context.Department ?? string.Empty,
        "phone" => context.Phone ?? string.Empty,
        "mobile" => context.Mobile ?? string.Empty,
        "fax" => context.Fax ?? string.Empty,
        "website" => context.Website ?? string.Empty,
        "tenant" or "company" => context.Tenant,
        _ => null,
    };

    /// <summary>The placeholders (lower case) that are known and have no value for this sender.</summary>
    private static HashSet<string> EmptyPlaceholders(SignatureContext context)
        => PlaceholderNames.Where(name => string.IsNullOrWhiteSpace(ValueOf(name, context))).ToHashSet(StringComparer.Ordinal);

    private static string DropEmptyTextLines(string template, SignatureContext context)
    {
        HashSet<string> empty = EmptyPlaceholders(context);
        return empty.Count == 0 ? template : DropEmptyTextLines(template, empty);
    }

    /// <summary>Plain text, line by line; the line ends of the text (CRLF or LF) stay what they were.</summary>
    private static string DropEmptyTextLines(string text, HashSet<string> empty)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string[] kept = lines.Where(line => !OnlyEmptyPlaceholders(NamesIn(line), empty)).ToArray();
        return kept.Length == lines.Length ? text : string.Join(newline, kept);
    }

    private static string DropEmptyHtmlLines(string template, SignatureContext context)
    {
        HashSet<string> empty = EmptyPlaceholders(context);
        if (empty.Count == 0 || !NamesIn(template).Any(empty.Contains))
        {
            return template;
        }

        IHtmlDocument document = new HtmlParser().ParseDocument("<body>" + template);
        if (document.Body is not { } body)
        {
            return template;
        }

        (bool dropped, _) = DropEmptyLines(body, empty);
        return dropped ? body.InnerHtml : template;
    }

    /// <summary>
    /// Goes through the lines of a block: the runs between line breaks and nested blocks. An inline wrapper that holds line breaks
    /// or blocks (a font element around a paragraph with &lt;br&gt;) is gone through as a block of its own, so one empty line in it
    /// does not take the whole wrapper along. Text in &lt;pre&gt; has its lines separated by newlines.
    /// Returns whether lines were left out, and whether nothing visible remains in the block (the caller then removes it as well).
    /// </summary>
    private static (bool Dropped, bool Emptied) DropEmptyLines(INode container, HashSet<string> empty)
    {
        bool dropped = false;
        var line = new List<INode>();

        foreach (INode child in container.ChildNodes.ToList())
        {
            if (child is IElement { LocalName: "br" } lineBreak)
            {
                dropped |= DropLine(line, lineBreak, empty);
                line.Clear();
            }
            else if (child is IElement { LocalName: "pre" } pre)
            {
                dropped |= DropLine(line, null, empty);
                line.Clear();
                if (DropEmptyPreLines(pre, empty))
                {
                    dropped = true;
                    if (!HasVisibleContent(pre))
                    {
                        pre.Remove();
                    }
                }
            }
            else if (child is IElement element && (BlockElements.Contains(element.LocalName) || element.QuerySelector(LineStructure) is not null))
            {
                dropped |= DropLine(line, null, empty);
                line.Clear();
                (bool innerDropped, bool innerEmptied) = DropEmptyLines(element, empty);
                dropped |= innerDropped;
                if (innerEmptied && IsRemovable(element))
                {
                    element.Remove();
                }
            }
            else
            {
                line.Add(child);
            }
        }

        dropped |= DropLine(line, null, empty);
        return (dropped, dropped && container is IElement self && !HasVisibleContent(self));
    }

    /// <summary>Block elements go when they are empty, inline wrappers too; the cells and rows of a table stay.</summary>
    private static bool IsRemovable(IElement element) => RemovableBlocks.Contains(element.LocalName) || !BlockElements.Contains(element.LocalName);

    private static bool DropEmptyPreLines(IElement pre, HashSet<string> empty)
    {
        bool dropped = false;
        foreach (IText text in SelfAndDescendants(pre).OfType<IText>().ToList())
        {
            string result = DropEmptyTextLines(text.Data, empty);
            if (result != text.Data)
            {
                text.Data = result;
                dropped = true;
            }
        }

        return dropped;
    }

    /// <summary>Removes a line (and the line break that ends it, or else the one before it) when all its placeholders are empty.</summary>
    private static bool DropLine(List<INode> line, IElement? lineBreak, HashSet<string> empty)
    {
        if (line.Count == 0 || !OnlyEmptyPlaceholders(line.SelectMany(SelfAndDescendants).SelectMany(NamesIn), empty))
        {
            return false;
        }

        INode? before = line[0].PreviousSibling;
        foreach (INode node in line)
        {
            node.Parent?.RemoveChild(node);
        }

        if (lineBreak is not null)
        {
            lineBreak.Remove();
        }
        else if (before is IElement { LocalName: "br" })
        {
            before.Parent?.RemoveChild(before);
        }

        return true;
    }

    private static bool OnlyEmptyPlaceholders(IEnumerable<string> names, HashSet<string> empty)
    {
        List<string> all = names.ToList();
        return all.Count > 0 && all.All(empty.Contains);
    }

    private static bool HasVisibleContent(IElement element)
        => !string.IsNullOrWhiteSpace(element.TextContent) || element.QuerySelector("img,hr,table,svg,video,iframe") is not null;

    private static IEnumerable<INode> SelfAndDescendants(INode node)
    {
        yield return node;
        foreach (INode child in node.ChildNodes)
        {
            foreach (INode descendant in SelfAndDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The placeholders (lower case) in a text, or in the text and the attributes of a node.</summary>
    private static IEnumerable<string> NamesIn(string text)
        => Placeholder().Matches(text).Select(match => match.Groups[1].Value.ToLowerInvariant());

    private static IEnumerable<string> NamesIn(INode node) => node switch
    {
        IText text => NamesIn(text.Data),
        IElement element => element.Attributes.SelectMany(attribute => NamesIn(attribute.Value)),
        _ => Enumerable.Empty<string>(),
    };

    /// <summary>
    /// HTML of a signature or footer for the page: nothing active in it, no forms and no positioning (see <see cref="EditorHtml"/>).
    /// Pictures may be embedded (data: addresses), nothing else.
    /// </summary>
    public static string Sanitize(string html)
    {
        Ganss.Xss.HtmlSanitizer sanitizer = EditorHtml.CreateSanitizer("mailto", "tel", "data");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !(e.Tag?.TagName == "IMG" && DataPicture().IsMatch(e.OriginalUrl)))
            {
                e.SanitizedUrl = null;
            }
        };
        return sanitizer.Sanitize(html);
    }

    /// <summary>
    /// A template (a signature, footer or mail template with {{Placeholders}}) without anything active in it. The placeholders are put
    /// aside while the sanitiser works, so they survive inside attributes ("mailto:{{Email}}") untouched.
    /// </summary>
    public static string SanitizeTemplate(string html)
    {
        string guarded = Placeholder().Replace(html, match => "mmph" + match.Groups[1].Value + "mmph");
        string clean = Sanitize(guarded);
        return GuardedPlaceholder().Replace(clean, match => "{{" + match.Groups[1].Value + "}}");
    }

    /// <summary>The plain-text version of a signature (its own text, or its HTML converted).</summary>
    public static string ToPlainText(Signature signature, SignatureContext context)
    {
        if (!string.IsNullOrWhiteSpace(signature.PlainText))
        {
            return Render(signature.PlainText, context, html: false);
        }

        return ToPlainText(Render(signature.Html, context, html: true));
    }

    private static string ToPlainText(string html)
    {
        try
        {
            return HtmlText.ToPlainText(html).Trim();
        }
        catch (Exception)
        {
            return Regex.Replace(html, "<[^>]+>", string.Empty).Trim();
        }
    }

    /// <summary>
    /// The signatures a user can choose from for a mailbox: tenant-wide, the mailbox' own and the user's own. The default ones come
    /// first, the most specific (user, mailbox, tenant) of them first of all: the web client preselects the first one.
    /// </summary>
    public async Task<IReadOnlyList<Signature>> GetSelectableAsync(long tenantId, long? mailboxId, long userId, CancellationToken cancel = default)
    {
        List<Signature> selectable = await _db.Signatures.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.Kind == SignatureKind.Signature
                        && (s.Scope == AppliesTo.Tenant
                            || (s.Scope == AppliesTo.Mailbox && s.MailboxId == mailboxId)
                            || (s.Scope == AppliesTo.User && s.UserId == userId)))
            .ToListAsync(cancel);
        return selectable.OrderByDescending(s => s.IsDefault).ThenByDescending(s => (int)s.Scope).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds what the server adds to an outgoing message: the signature of the sender when it is set to be added on the server and the
    /// message does not carry it yet (mail programs only), then every footer that applies (tenant-wide, the sending mailbox', the
    /// sending user's), at the end of the text. Signed and encrypted messages are left as they are, and so are attachments that only
    /// look like text.
    /// </summary>
    public async Task ApplyAsync(
        MimeMessage message, SubmissionSource source, long tenantId, long? mailboxId, long? userId, SignatureContext context, CancellationToken cancel = default)
    {
        if (MessageContent.IsProtected(message))
        {
            return;
        }

        List<Signature> candidates = (await _db.Signatures.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.IsActive && (s.Kind == SignatureKind.Footer || s.AddOnServer)
                            && (s.Scope == AppliesTo.Tenant
                                || (s.Scope == AppliesTo.Mailbox && s.MailboxId == mailboxId)
                                || (s.Scope == AppliesTo.User && s.UserId == userId)))
                .ToListAsync(cancel))
            .OrderBy(s => (int)s.Scope).ThenBy(s => s.Id)
            .ToList();

        // What each of them comes to for this sender; one that comes to nothing is not added at all (no empty "-- " separator).
        Signature? chosen = source == SubmissionSource.Web ? null : ChooseSignature(candidates.Where(s => s.Kind == SignatureKind.Signature));
        Rendered? signature = chosen is null ? null : RenderFor(chosen, context);
        List<Rendered> footers = candidates.Where(s => s.Kind == SignatureKind.Footer).Select(f => RenderFor(f, context)).Where(r => !r.IsBlank).ToList();
        if (signature is { IsBlank: true })
        {
            signature = null;
        }

        if (footers.Count == 0 && signature is null)
        {
            return;
        }

        // The end of the text: the last part of each kind (a message that is cut into pieces by pictures is signed after its last piece).
        IReadOnlyList<TextPart> bodies = MessageContent.BodyParts(message);
        TextPart? html = bodies.LastOrDefault(p => p.IsHtml);
        TextPart? plain = bodies.LastOrDefault(p => p.IsPlain);
        var pictures = new List<MimePart>();

        // The sender signed already, as their mail program does it: then neither version of the text gets another one (they are the
        // same message, and the program need not have written the signature into both the same way).
        if (signature is not null
            && ((html is not null && HtmlCarriesSignature(html.Text, signature.Plain)) || (plain is not null && PlainCarriesSignature(plain.Text, signature.Plain))))
        {
            signature = null;
        }

        if (html is not null)
        {
            string addition = string.Empty;
            if (signature is { Html.Length: > 0 })
            {
                addition += "<div class=\"mm-signature\">" + signature.Html + "</div>";
            }

            addition += string.Concat(footers.Select(f => "<div class=\"mm-footer\">" + f.Html + "</div>"));
            if (addition.Length > 0)
            {
                addition = InlinePictures.Reference(addition, pictures);
                string text = html.Text;
                int body = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                MessageContent.SetText(html, body >= 0 ? text.Insert(body, addition) : text + addition);
            }
        }

        if (plain is not null)
        {
            string text = plain.Text.TrimEnd();
            string original = text;
            if (signature is { Plain.Length: > 0 })
            {
                text += "\r\n\r\n-- \r\n" + signature.Plain;
            }

            string[] footerTexts = footers.Select(f => f.Plain).Where(t => t.Length > 0).ToArray();
            if (footerTexts.Length > 0)
            {
                text += "\r\n\r\n-- \r\n" + string.Join("\r\n\r\n", footerTexts);
            }

            if (text != original)
            {
                MessageContent.SetText(plain, text + "\r\n");
            }
        }

        if (html is not null)
        {
            InlinePictures.Attach(message, html, pictures);
        }
    }

    /// <summary>A signature or footer as it comes out for one sender, as HTML and as text.</summary>
    private sealed record Rendered(string Html, string Plain)
    {
        /// <summary>Nothing to show: no text and no picture.</summary>
        public bool IsBlank => Plain.Length == 0 && !Html.Contains("<img", StringComparison.OrdinalIgnoreCase);
    }

    private static Rendered RenderFor(Signature signature, SignatureContext context)
    {
        string html = Render(signature.Html, context, html: true);
        string plain = ToPlainText(signature, context);
        return new Rendered(string.IsNullOrWhiteSpace(plain) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase) ? string.Empty : html, plain);
    }

    /// <summary>The signature that applies: the user's own, else the mailbox's, else the tenant's; the default one first.</summary>
    private static Signature? ChooseSignature(IEnumerable<Signature> signatures)
        => signatures.OrderByDescending(s => s.Scope == AppliesTo.User).ThenByDescending(s => s.Scope == AppliesTo.Mailbox)
            .ThenByDescending(s => s.IsDefault).ThenBy(s => s.Id).FirstOrDefault();

    /// <summary>
    /// Whether the message already carries a signature: one that a mail program put there (see <see cref="SignatureDetector"/>) or the
    /// text of ours. What is quoted from an older message does not count: a reply to a message that was signed is not signed itself.
    /// </summary>
    private static bool HtmlCarriesSignature(string html, string signatureText)
        => SignatureDetector.HtmlCarries(html) || ContainsText(SignatureDetector.WrittenText(html), signatureText);

    /// <summary>Plain text: quoted lines (those that begin with "&gt;") do not count.</summary>
    private static bool PlainCarriesSignature(string text, string signatureText)
        => SignatureDetector.PlainCarries(text)
           || ContainsText(string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('>'))), signatureText);

    /// <summary>Whether the snippet is in the text, whatever the line breaks and the spacing.</summary>
    private static bool ContainsText(string text, string snippet)
    {
        string wanted = Whitespace().Replace(snippet, " ").Trim();
        return wanted.Length > 0 && Whitespace().Replace(text, " ").Contains(wanted, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"mmph(\w+?)mmph")]
    private static partial Regex GuardedPlaceholder();

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex DataPicture();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Placeholder();
}

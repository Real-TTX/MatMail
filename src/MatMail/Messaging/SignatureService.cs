using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
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
    private static readonly string[] PlaceholderNames =
    {
        "displayname", "name", "fullname", "firstname", "lastname", "salutation", "title", "email", "jobtitle", "position", "department",
        "phone", "mobile", "fax", "website", "tenant", "company",
    };

    /// <summary>Elements that start a new line of their own; a line break (&lt;br&gt;) ends a line inside one.</summary>
    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "ul", "ol", "table", "tbody", "thead", "tfoot", "tr", "td", "th", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "address", "pre",
    };

    /// <summary>Block elements that disappear when their last line was left out (cells and rows stay: a table keeps its shape).</summary>
    private static readonly HashSet<string> RemovableBlocks = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "ul", "ol", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer", "address", "pre",
    };

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
        if (empty.Count == 0)
        {
            return template;
        }

        string[] lines = template.Split('\n');
        string[] kept = lines.Where(line => !OnlyEmptyPlaceholders(NamesIn(line), empty)).ToArray();
        return kept.Length == lines.Length ? template : string.Join('\n', kept);
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

        return DropEmptyLines(body, empty) || body.InnerHtml.Length != template.Length ? body.InnerHtml : template;
    }

    /// <summary>
    /// Goes through the lines of a block: the runs between line breaks and nested blocks. Returns true when lines were left out and
    /// nothing visible remains in the block (the caller then removes the block as well).
    /// </summary>
    private static bool DropEmptyLines(INode container, HashSet<string> empty)
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
            else if (child is IElement block && BlockElements.Contains(block.LocalName))
            {
                dropped |= DropLine(line, null, empty);
                line.Clear();
                if (DropEmptyLines(block, empty) && RemovableBlocks.Contains(block.LocalName))
                {
                    block.Remove();
                    dropped = true;
                }
            }
            else
            {
                line.Add(child);
            }
        }

        dropped |= DropLine(line, null, empty);
        return dropped && container is IElement element && !HasVisibleContent(element);
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

    /// <summary>HTML of a signature or footer without anything active in it. Pictures may be embedded (data: addresses), nothing else.</summary>
    public static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer { AllowedSchemes = { "mailto", "tel", "data" } };
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

        string html = Render(signature.Html, context, html: true);
        try
        {
            return HtmlText.ToPlainText(html).Trim();
        }
        catch (Exception)
        {
            return Regex.Replace(html, "<[^>]+>", string.Empty).Trim();
        }
    }

    /// <summary>The signatures a user can choose from for a mailbox: tenant-wide, the mailbox' own and the user's own.</summary>
    public async Task<IReadOnlyList<Signature>> GetSelectableAsync(long tenantId, long? mailboxId, long userId, CancellationToken cancel = default)
        => await _db.Signatures.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.Kind == SignatureKind.Signature
                        && (s.Scope == SignatureScope.Tenant
                            || (s.Scope == SignatureScope.Mailbox && s.MailboxId == mailboxId)
                            || (s.Scope == SignatureScope.User && s.UserId == userId)))
            .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name)
            .ToListAsync(cancel);

    /// <summary>
    /// Adds what the server adds to an outgoing message: the signature of the sender when it is set to be added on the server and the
    /// message does not carry it yet (mail programs only), then every footer that applies (tenant-wide, the sending mailbox', the
    /// sending user's). Signed and encrypted messages are left as they are.
    /// </summary>
    public async Task ApplyAsync(
        MimeMessage message, SubmissionSource source, long tenantId, long? mailboxId, long? userId, SignatureContext context, CancellationToken cancel = default)
    {
        if (MessageContent.IsProtected(message))
        {
            return;
        }

        List<Signature> candidates = await _db.Signatures.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && (s.Kind == SignatureKind.Footer || s.AddOnServer)
                        && (s.Scope == SignatureScope.Tenant
                            || (s.Scope == SignatureScope.Mailbox && s.MailboxId == mailboxId)
                            || (s.Scope == SignatureScope.User && s.UserId == userId)))
            .OrderBy(s => s.Scope).ThenBy(s => s.Id)
            .ToListAsync(cancel);

        List<Signature> footers = candidates.Where(s => s.Kind == SignatureKind.Footer).ToList();
        Signature? signature = source == SubmissionSource.Web ? null : ChooseSignature(candidates.Where(s => s.Kind == SignatureKind.Signature));
        if (footers.Count == 0 && signature is null)
        {
            return;
        }

        var pictures = new List<MimePart>();
        TextPart? firstHtml = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => !p.IsAttachment && p.IsHtml);
        foreach (TextPart part in message.BodyParts.OfType<TextPart>().Where(p => !p.IsAttachment))
        {
            if (part.IsHtml)
            {
                string html = string.Empty;
                if (signature is not null && !HasSignatureMarker(part.Text))
                {
                    html += "<div class=\"mm-signature\">" + Render(signature.Html, context, html: true) + "</div>";
                }

                html += string.Concat(footers.Select(f => "<div class=\"mm-footer\">" + Render(f.Html, context, html: true) + "</div>"));
                if (html.Length > 0)
                {
                    if (ReferenceEquals(part, firstHtml))
                    {
                        html = InlinePictures.Reference(html, pictures);
                    }

                    int body = part.Text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    part.Text = body >= 0 ? part.Text.Insert(body, html) : part.Text + html;
                }
            }
            else if (part.IsPlain)
            {
                string text = part.Text.TrimEnd();
                if (signature is not null && !ContainsText(text, ToPlainText(signature, context)))
                {
                    text += "\r\n\r\n-- \r\n" + ToPlainText(signature, context);
                }

                if (footers.Count > 0)
                {
                    text += "\r\n\r\n-- \r\n" + string.Join("\r\n\r\n", footers.Select(f => ToPlainText(f, context)));
                }

                part.Text = text + "\r\n";
            }
        }

        if (firstHtml is not null)
        {
            InlinePictures.Attach(message, firstHtml, pictures);
        }
    }

    /// <summary>The signature that applies: the user's own, else the mailbox's, else the tenant's; the default one first.</summary>
    private static Signature? ChooseSignature(IEnumerable<Signature> signatures)
        => signatures.OrderByDescending(s => s.Scope == SignatureScope.User).ThenByDescending(s => s.Scope == SignatureScope.Mailbox)
            .ThenByDescending(s => s.IsDefault).ThenBy(s => s.Id).FirstOrDefault();

    /// <summary>The web client wraps the signature it inserts in a block of this class.</summary>
    private static bool HasSignatureMarker(string html) => SignatureMarker().IsMatch(html);

    private static bool ContainsText(string text, string snippet)
        => !string.IsNullOrWhiteSpace(snippet) && text.Replace("\r\n", "\n").Contains(snippet.Trim().Replace("\r\n", "\n"), StringComparison.Ordinal);

    [GeneratedRegex(@"mmph(\w+?)mmph")]
    private static partial Regex GuardedPlaceholder();

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex DataPicture();

    [GeneratedRegex(@"class=""[^""]*\bmm-signature\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureMarker();

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Placeholder();
}

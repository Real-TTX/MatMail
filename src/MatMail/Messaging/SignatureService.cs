using System.Net;
using System.Text.RegularExpressions;
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
/// Signatures and footers. A <see cref="SignatureKind.Signature"/> is offered by the mail client; a <see cref="SignatureKind.Footer"/>
/// is appended here to every outgoing message in its scope, whatever client sent it (web, Outlook, a device through the smart host).
/// </summary>
public sealed partial class SignatureService
{
    private readonly MatMailDbContext _db;

    public SignatureService(MatMailDbContext db) => _db = db;

    /// <summary>Replaces {{Placeholders}}. Values are HTML-encoded for HTML templates.</summary>
    public static string Render(string template, SignatureContext context, bool html)
    {
        string Value(string? text) => html ? WebUtility.HtmlEncode(text ?? string.Empty) : text ?? string.Empty;
        string rendered = Placeholder().Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "displayname" or "name" => Value(context.DisplayName),
            "fullname" => Value(context.FullName),
            "firstname" => Value(context.FirstNameOrDerived),
            "lastname" => Value(context.LastNameOrDerived),
            "salutation" => Value(context.Salutation),
            "title" => Value(context.Title),
            "email" => Value(context.Email),
            "jobtitle" or "position" => Value(context.JobTitle),
            "department" => Value(context.Department),
            "phone" => Value(context.Phone),
            "mobile" => Value(context.Mobile),
            "fax" => Value(context.Fax),
            "website" => Value(context.Website),
            "tenant" or "company" => Value(context.Tenant),
            _ => match.Value,
        });

        // Signatures are written by administrators but shown inside the mail client of every user (it inserts them into the message
        // being written), so they get the same treatment as received mail: no scripts, event handlers or script URLs.
        return html ? Sanitize(rendered) : rendered;
    }

    /// <summary>HTML of a signature or footer without anything active in it.</summary>
    public static string Sanitize(string html) => new HtmlSanitizer { AllowedSchemes = { "mailto", "tel" } }.Sanitize(html);

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

    /// <summary>Appends every footer that applies to the message (tenant-wide, the sending mailbox', the sending user's).</summary>
    public async Task ApplyFootersAsync(MimeMessage message, long tenantId, long? mailboxId, long? userId, SignatureContext context, CancellationToken cancel = default)
    {
        List<Signature> footers = await _db.Signatures.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.Kind == SignatureKind.Footer
                        && (s.Scope == SignatureScope.Tenant
                            || (s.Scope == SignatureScope.Mailbox && s.MailboxId == mailboxId)
                            || (s.Scope == SignatureScope.User && s.UserId == userId)))
            .OrderBy(s => s.Scope).ThenBy(s => s.Id)
            .ToListAsync(cancel);
        if (footers.Count == 0)
        {
            return;
        }

        string footerHtml = string.Concat(footers.Select(f => "<div class=\"mm-footer\">" + Render(f.Html, context, html: true) + "</div>"));
        string footerText = string.Join("\r\n\r\n", footers.Select(f => ToPlainText(f, context)));

        foreach (TextPart part in message.BodyParts.OfType<TextPart>().Where(p => !p.IsAttachment))
        {
            if (part.IsHtml)
            {
                string html = part.Text;
                int body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                part.Text = body >= 0 ? html.Insert(body, footerHtml) : html + footerHtml;
            }
            else if (part.IsPlain)
            {
                part.Text = part.Text.TrimEnd() + "\r\n\r\n-- \r\n" + footerText + "\r\n";
            }
        }
    }

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Placeholder();
}

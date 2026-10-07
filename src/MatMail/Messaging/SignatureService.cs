using System.Net;
using System.Text.RegularExpressions;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;


namespace MatMail.Messaging;

/// <summary>The values a signature template can use.</summary>
public sealed record SignatureContext(string DisplayName, string Email, string? JobTitle, string? Phone, string Tenant);

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
        return Placeholder().Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "displayname" or "name" => Value(context.DisplayName),
            "email" => Value(context.Email),
            "jobtitle" => Value(context.JobTitle),
            "phone" => Value(context.Phone),
            "tenant" or "company" => Value(context.Tenant),
            _ => match.Value,
        });
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

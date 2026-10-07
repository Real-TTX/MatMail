using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Messaging;

public sealed record ContactSuggestion(string Name, string Address);

/// <summary>Address suggestions while typing a recipient: the tenant's own addresses first, then people the user corresponds with.</summary>
public sealed class ContactService
{
    private readonly MatMailDbContext _db;
    private readonly MailAccessService _access;

    public ContactService(MatMailDbContext db, MailAccessService access)
    {
        _db = db;
        _access = access;
    }

    public async Task<IReadOnlyList<ContactSuggestion>> SuggestAsync(MailUser user, string? text, int max = 8, CancellationToken cancel = default)
    {
        string query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return Array.Empty<ContactSuggestion>();
        }

        string pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var result = new List<ContactSuggestion>();

        // The tenant's directory: every mailbox address (shared mailboxes are the usual "info@" targets).
        var directory = await _db.MailboxAliases.AsNoTracking()
            .Where(a => a.TenantId == user.TenantId && !a.Address.StartsWith("*@") && a.Mailbox!.Type != MailboxType.Unassigned
                        && (EF.Functions.ILike(a.Address, pattern) || EF.Functions.ILike(a.Mailbox.Name, pattern)))
            .OrderBy(a => a.Address)
            .Select(a => new { a.Mailbox!.Name, a.Address })
            .Take(max)
            .ToListAsync(cancel);
        result.AddRange(directory.Select(d => new ContactSuggestion(d.Name, d.Address)));

        // People from the mail the user can see: senders of received mail and recipients of sent mail.
        long[] mailboxIds = (await _access.GetMailboxesAsync(user, cancel)).Select(m => m.Mailbox.Id).ToArray();
        var senders = await _db.MailMessages.AsNoTracking()
            .Where(m => mailboxIds.Contains(m.MailboxId) && m.FromAddress != string.Empty && !m.IsDraft
                        && (EF.Functions.ILike(m.FromAddress, pattern) || EF.Functions.ILike(m.FromName, pattern)))
            .GroupBy(m => new { m.FromAddress, m.FromName })
            .OrderByDescending(g => g.Count())
            .Select(g => new { g.Key.FromName, g.Key.FromAddress })
            .Take(max * 2)
            .ToListAsync(cancel);
        result.AddRange(senders.Select(s => new ContactSuggestion(s.FromName, s.FromAddress)));

        List<string> recipients = await _db.MailMessages.AsNoTracking()
            .Where(m => mailboxIds.Contains(m.MailboxId) && m.Folder!.Kind == FolderKind.Sent && EF.Functions.ILike(m.ToSummary, pattern))
            .OrderByDescending(m => m.ReceivedDate)
            .Select(m => m.ToSummary)
            .Take(50)
            .ToListAsync(cancel);
        foreach (string summary in recipients)
        {
            if (InternetAddressList.TryParse(summary, out InternetAddressList? list))
            {
                foreach (MailboxAddress mailbox in list.Mailboxes.Where(m => Matches(m, query)))
                {
                    result.Add(new ContactSuggestion(mailbox.Name ?? string.Empty, mailbox.Address));
                }
            }
        }

        return result
            .GroupBy(c => c.Address.ToLowerInvariant())
            .Select(g => g.OrderByDescending(c => c.Name.Length).First())
            .Take(max)
            .ToList();
    }

    private static bool Matches(MailboxAddress mailbox, string query)
        => mailbox.Address.Contains(query, StringComparison.OrdinalIgnoreCase) || (mailbox.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}

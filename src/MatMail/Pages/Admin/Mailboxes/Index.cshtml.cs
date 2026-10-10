using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Mailboxes;

public class IndexModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<MailboxRow> Paged { get; private set; } = new(Array.Empty<MailboxRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record MailboxRow(
        long Id, string Name, MailboxType Type, bool IsActive, string? OwnerName, string? PrimaryAddress, int AddressCount, int DelegateCount, int MessageCount, long UsedBytes, long? QuotaBytes);

    public async Task OnGetAsync()
    {
        IQueryable<Mailbox> query = db.Mailboxes.AsNoTracking().Where(m => m.Type != MailboxType.Unassigned);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(m => EF.Functions.ILike(m.Name, pattern)
                                     || db.MailboxAliases.Any(a => a.MailboxId == m.Id && EF.Functions.ILike(a.Address, pattern))
                                     || (m.OwnerUser != null && EF.Functions.ILike(m.OwnerUser.DisplayName, pattern)));
        }

        if (Enum.TryParse(Type, out MailboxType type) && type != MailboxType.Unassigned)
        {
            query = query.Where(m => m.Type == type);
        }

        query = Status switch
        {
            "active" => query.Where(m => m.IsActive),
            "inactive" => query.Where(m => !m.IsActive),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(m => m.Name),
            "messages_desc" => query.OrderByDescending(m => db.MailMessages.Count(x => x.MailboxId == m.Id)).ThenBy(m => m.Name),
            "size_desc" => query.OrderByDescending(m => db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes)).ThenBy(m => m.Name),
            "created_desc" => query.OrderByDescending(m => m.CreateDate),
            _ => query.OrderBy(m => m.Name),
        };

        Paged = await query
            .Select(m => new MailboxRow(
                m.Id,
                m.Name,
                m.Type,
                m.IsActive,
                m.OwnerUser != null ? m.OwnerUser.DisplayName : null,
                db.MailboxAliases.Where(a => a.MailboxId == m.Id).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address).Select(a => a.Address).FirstOrDefault(),
                db.MailboxAliases.Count(a => a.MailboxId == m.Id),
                db.MailboxPermissions.Count(p => p.MailboxId == m.Id),
                db.MailMessages.Count(x => x.MailboxId == m.Id),
                db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes),
                m.QuotaBytes))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

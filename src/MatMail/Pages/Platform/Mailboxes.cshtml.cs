using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Platform;

public class MailboxesModel(MatMailDbContext db) : PlatformListModel(db)
{
    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    public PageResult<MailboxRow> Paged { get; private set; } = new(Array.Empty<MailboxRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record MailboxRow(
        long Id, long TenantId, string Name, MailboxType Type, bool IsActive, string? OwnerName, string? PrimaryAddress, int AddressCount, int MessageCount, long UsedBytes, long? QuotaBytes);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        IQueryable<Mailbox> query = Db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.Type != MailboxType.Unassigned);
        if (TenantId is long tenantId)
        {
            query = query.Where(m => m.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(m => EF.Functions.ILike(m.Name, pattern)
                                     || Db.MailboxAliases.Any(a => a.MailboxId == m.Id && EF.Functions.ILike(a.Address, pattern))
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
            "full" => query.Where(m => m.QuotaBytes != null && Db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes) >= m.QuotaBytes),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(m => m.Name),
            "size_desc" => query.OrderByDescending(m => Db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes)).ThenBy(m => m.Name),
            "created_desc" => query.OrderByDescending(m => m.CreateDate),
            _ => query.OrderBy(m => m.Name),
        };

        Paged = await query
            .Select(m => new MailboxRow(
                m.Id,
                m.TenantId,
                m.Name,
                m.Type,
                m.IsActive,
                m.OwnerUser != null ? m.OwnerUser.DisplayName : null,
                Db.MailboxAliases.Where(a => a.MailboxId == m.Id).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address).Select(a => a.Address).FirstOrDefault(),
                Db.MailboxAliases.Count(a => a.MailboxId == m.Id),
                Db.MailMessages.Count(x => x.MailboxId == m.Id),
                Db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes),
                m.QuotaBytes))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

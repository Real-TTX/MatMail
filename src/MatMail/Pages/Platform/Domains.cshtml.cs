using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Platform;

public class DomainsModel(MatMailDbContext db) : PlatformListModel(db)
{
    public PageResult<DomainRow> Paged { get; private set; } = new(Array.Empty<DomainRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record DomainRow(long Id, long TenantId, string Name, string? Notes, bool IsActive, string? CatchAllMailbox, int Addresses);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        IQueryable<Domain> query = Db.Domains.IgnoreQueryFilters().AsNoTracking();
        if (TenantId is long tenantId)
        {
            query = query.Where(d => d.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(d => EF.Functions.ILike(d.Name, pattern) || (d.Notes != null && EF.Functions.ILike(d.Notes, pattern)));
        }

        query = Status switch
        {
            "active" => query.Where(d => d.IsActive),
            "inactive" => query.Where(d => !d.IsActive),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(d => d.Name),
            "addresses_desc" => query.OrderByDescending(d => Db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))).ThenBy(d => d.Name),
            _ => query.OrderBy(d => d.Name),
        };

        Paged = await query
            .Select(d => new DomainRow(
                d.Id, d.TenantId, d.Name, d.Notes, d.IsActive,
                d.CatchAllMailbox != null ? d.CatchAllMailbox.Name : null,
                Db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

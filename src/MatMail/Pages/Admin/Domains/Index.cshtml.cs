using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Domains;

public class IndexModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<DomainRow> Paged { get; private set; } = new(Array.Empty<DomainRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record DomainRow(long Id, string Name, string? Notes, bool IsActive, string? CatchAllMailbox, int Addresses);

    public async Task OnGetAsync()
    {
        IQueryable<Domain> query = db.Domains.AsNoTracking();
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
            "addresses_desc" => query.OrderByDescending(d => db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))).ThenBy(d => d.Name),
            _ => query.OrderBy(d => d.Name),
        };

        Paged = await query
            .Select(d => new DomainRow(
                d.Id, d.Name, d.Notes, d.IsActive,
                d.CatchAllMailbox != null ? d.CatchAllMailbox.Name : null,
                db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

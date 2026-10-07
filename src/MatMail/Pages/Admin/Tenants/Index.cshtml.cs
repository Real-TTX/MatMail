using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Tenants;

public class IndexModel(MatMailDbContext db, CurrentUser currentUser) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<TenantRow> Paged { get; private set; } = new(Array.Empty<TenantRow>(), 0, 1, 1, Pager.DefaultPageSize);
    public long? CurrentTenantId => currentUser.TenantId;

    public sealed record TenantRow(long Id, string Name, string? Description, bool IsActive, DateTime CreateDate, int Users, int Mailboxes);

    public async Task OnGetAsync()
    {
        IQueryable<Tenant> query = db.Tenants.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern) || (t.Description != null && EF.Functions.ILike(t.Description, pattern)));
        }

        if (Status == "active")
        {
            query = query.Where(t => t.IsActive);
        }
        else if (Status == "inactive")
        {
            query = query.Where(t => !t.IsActive);
        }

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(t => t.Name),
            "created_desc" => query.OrderByDescending(t => t.CreateDate),
            _ => query.OrderBy(t => t.Name),
        };

        Paged = await query
            .Select(t => new TenantRow(
                t.Id,
                t.Name,
                t.Description,
                t.IsActive,
                t.CreateDate,
                db.Users.IgnoreQueryFilters().Count(u => u.TenantId == t.Id),
                db.Mailboxes.IgnoreQueryFilters().Count(m => m.TenantId == t.Id && m.Type != MailboxType.Unassigned)))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

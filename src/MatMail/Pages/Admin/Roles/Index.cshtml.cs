using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Roles;

public class IndexModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<RoleRow> Paged { get; private set; } = new(Array.Empty<RoleRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record RoleRow(long Id, string Name, string? Description, bool IsBuiltIn, int PermissionCount, int Users);

    public async Task OnGetAsync()
    {
        IQueryable<Role> query = db.Roles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern) || (r.Description != null && EF.Functions.ILike(r.Description, pattern)));
        }

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(r => r.Name),
            "users_desc" => query.OrderByDescending(r => db.UserRoles.Count(ur => ur.RoleId == r.Id)).ThenBy(r => r.Name),
            _ => query.OrderBy(r => r.Name),
        };

        Paged = await query
            .Select(r => new RoleRow(r.Id, r.Name, r.Description, r.IsBuiltIn, r.Permissions.Length, db.UserRoles.Count(ur => ur.RoleId == r.Id)))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

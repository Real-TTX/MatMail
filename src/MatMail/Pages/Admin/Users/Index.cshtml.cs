using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Users;

public class IndexModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? RoleId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<UserRow> Paged { get; private set; } = new(Array.Empty<UserRow>(), 0, 1, 1, Pager.DefaultPageSize);
    public IReadOnlyList<Role> Roles { get; private set; } = Array.Empty<Role>();

    public sealed record UserRow(
        long Id, string LoginName, string DisplayName, string? Email, bool IsActive, bool IsSystemAdmin,
        DateTime? LastLoginDate, DateTime? LockedUntilDate, List<string> Roles);

    public async Task OnGetAsync()
    {
        Roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        DateTime now = DateTime.UtcNow;

        IQueryable<User> query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.LoginName, pattern)
                                     || (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        if (RoleId is long roleId)
        {
            query = query.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId));
        }

        query = Status switch
        {
            "active" => query.Where(u => u.IsActive),
            "inactive" => query.Where(u => !u.IsActive),
            "locked" => query.Where(u => u.LockedUntilDate > now),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(u => u.DisplayName),
            "login_asc" => query.OrderBy(u => u.LoginName),
            "lastlogin_desc" => query.OrderByDescending(u => u.LastLoginDate),
            "created_desc" => query.OrderByDescending(u => u.CreateDate),
            _ => query.OrderBy(u => u.DisplayName),
        };

        Paged = await query
            .Select(u => new UserRow(
                u.Id, u.LoginName, u.DisplayName, u.Email, u.IsActive, u.IsSystemAdmin, u.LastLoginDate, u.LockedUntilDate,
                db.UserRoles.Where(ur => ur.UserId == u.Id).Select(ur => ur.Role!.Name).OrderBy(n => n).ToList()))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

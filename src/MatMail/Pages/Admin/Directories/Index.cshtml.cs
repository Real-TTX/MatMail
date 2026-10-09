using MatMail.Data;
using MatMail.Directories;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Directories;

public class IndexModel(MatMailDbContext db, DirectoryProvisioner provisioner, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public sealed record DirectoryRow(DirectoryConnection Directory, int Users, int Blocked);

    public PageResult<DirectoryRow> Paged { get; private set; } = new(Array.Empty<DirectoryRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        IQueryable<DirectoryConnection> query = db.DirectoryConnections.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(d => EF.Functions.ILike(d.Name, pattern) || EF.Functions.ILike(d.Host, pattern) || EF.Functions.ILike(d.BaseDn, pattern));
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
            "sync_desc" => query.OrderByDescending(d => d.LastSyncDate),
            "created_desc" => query.OrderByDescending(d => d.CreateDate),
            _ => query.OrderBy(d => d.Name),
        };

        Paged = await query
            .Select(d => new DirectoryRow(
                d,
                db.Users.Count(u => u.DirectoryId == d.Id),
                db.Users.Count(u => u.DirectoryId == d.Id && u.DirectoryDisabledDate != null)))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }

    /// <summary>Compares the users of a directory with it now.</summary>
    public async Task<IActionResult> OnPostCompareAsync(long id)
    {
        DirectoryConnection? dir = await db.DirectoryConnections.FirstOrDefaultAsync(d => d.Id == id);
        if (dir is null)
        {
            return NotFound();
        }

        DirectorySyncResult result = await provisioner.SyncAsync(dir, HttpContext.RequestAborted);
        this.Notify(result.Ok, result.Message);
        return RedirectToPage();
    }

    public string SecurityLabel(DirectorySecurity security) => security switch
    {
        DirectorySecurity.Ldaps => "LDAPS",
        DirectorySecurity.StartTls => "STARTTLS",
        _ => l["Not encrypted"].Value,
    };
}

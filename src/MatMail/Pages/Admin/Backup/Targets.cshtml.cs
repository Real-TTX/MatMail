using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Backup;

/// <summary>The places backups are written to.</summary>
public class TargetsModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<BackupTarget> Paged { get; private set; } = new(Array.Empty<BackupTarget>(), 0, 1, 1, Pager.DefaultPageSize);

    /// <summary>How many schedules write to each target on the page.</summary>
    public Dictionary<long, int> PlanCounts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        IQueryable<BackupTarget> query = db.BackupTargets.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Path, pattern) || (t.Host != null && EF.Functions.ILike(t.Host, pattern)));
        }

        query = Kind switch
        {
            "local" => query.Where(t => t.Kind == BackupTargetKind.Local),
            "smb" => query.Where(t => t.Kind == BackupTargetKind.Smb),
            _ => query,
        };

        query = Sort == "name_desc" ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name);

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;

        long[] ids = Paged.Rows.Select(t => t.Id).ToArray();
        PlanCounts = await db.BackupPlans.AsNoTracking().Where(p => ids.Contains(p.TargetId)).GroupBy(p => p.TargetId).ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    public static string Where(BackupTarget target)
        => target.Kind == BackupTargetKind.Smb
            ? "\\\\" + target.Host + "\\" + target.Share + (string.IsNullOrWhiteSpace(target.Path) ? string.Empty : "\\" + target.Path.Trim('/', '\\').Replace('/', '\\'))
            : target.Path;
}

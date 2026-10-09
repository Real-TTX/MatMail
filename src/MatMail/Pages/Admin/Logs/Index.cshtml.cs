using System.Globalization;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Logs;

public class IndexModel(MatMailDbContext db, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Level { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "newest";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<ActivityLog> Paged { get; private set; } = new(Array.Empty<ActivityLog>(), 0, 1, 1, PageSize);

    public async Task OnGetAsync()
    {
        long? tenantId = currentUser.TenantId;
        bool system = currentUser.IsSystemAdmin;

        // A tenant sees its own events; installation-wide events (no tenant) are for system administrators.
        IQueryable<ActivityLog> query = db.ActivityLogs.AsNoTracking().Where(a => a.TenantId == tenantId || (system && a.TenantId == null));

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(a => EF.Functions.ILike(a.Message, pattern) || (a.Details != null && EF.Functions.ILike(a.Details, pattern))
                                     || (a.RemoteIp != null && EF.Functions.ILike(a.RemoteIp, pattern)));
        }

        if (Enum.TryParse(Category, out ActivityCategory category))
        {
            query = query.Where(a => a.Category == category);
        }

        if (Enum.TryParse(Level, out ActivityLevel level))
        {
            query = query.Where(a => a.Level == level);
        }

        if (DateTime.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime from))
        {
            query = query.Where(a => a.CreateDate >= from);
        }

        if (DateTime.TryParseExact(To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime to))
        {
            DateTime end = to.AddDays(1);
            query = query.Where(a => a.CreateDate < end);
        }

        query = Sort == "oldest" ? query.OrderBy(a => a.CreateDate) : query.OrderByDescending(a => a.CreateDate);
        Paged = await query.ToPageAsync(PageNumber, PageSize);
        PageNumber = Paged.PageNumber;
    }

    public string CategoryLabel(string name) => name switch
    {
        nameof(ActivityCategory.Auth) => l["Sign-in"].Value,
        nameof(ActivityCategory.Admin) => l["Administration"].Value,
        nameof(ActivityCategory.Smtp) => "SMTP",
        nameof(ActivityCategory.Imap) => "IMAP",
        nameof(ActivityCategory.Sync) => l["Synchronisation"].Value,
        nameof(ActivityCategory.Queue) => l["Delivery"].Value,
        nameof(ActivityCategory.Backup) => l["Backups"].Value,
        _ => l["System"].Value,
    };
}

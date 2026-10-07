using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Accounts;

public class IndexModel(MatMailDbContext db, IAccountSyncRunner sync, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<MailAccount> Paged { get; private set; } = new(Array.Empty<MailAccount>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        IQueryable<MailAccount> query = db.MailAccounts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Address, pattern)
                                     || (a.ReceiveHost != null && EF.Functions.ILike(a.ReceiveHost, pattern))
                                     || (a.SendHost != null && EF.Functions.ILike(a.SendHost, pattern)));
        }

        if (Enum.TryParse(Role, out MailAccountRole role))
        {
            query = query.Where(a => a.Role == role);
        }

        query = Status switch
        {
            "enabled" => query.Where(a => a.IsEnabled),
            "disabled" => query.Where(a => !a.IsEnabled),
            "error" => query.Where(a => a.LastSyncState == SyncState.Error),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(a => a.Name),
            "sync_desc" => query.OrderByDescending(a => a.LastSyncDate),
            "created_desc" => query.OrderByDescending(a => a.CreateDate),
            _ => query.OrderBy(a => a.Name),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }

    public async Task<IActionResult> OnPostSyncAsync(long id)
    {
        if (!await db.MailAccounts.AnyAsync(a => a.Id == id))
        {
            return NotFound();
        }

        SyncOutcome outcome = await sync.SyncNowAsync(id, HttpContext.RequestAborted);
        this.Notify(outcome.Ok, outcome.Message);
        return RedirectToPage();
    }

    public string RoleLabel(MailAccountRole role) => role switch
    {
        MailAccountRole.Mail => l["Mail"].Value,
        MailAccountRole.Backup => l["Backup"].Value,
        MailAccountRole.Migration => l["Migration"].Value,
        _ => l["Send only"].Value,
    };

    public string RetentionLabel(MailAccountRole role, ServerRetention retention) => role == MailAccountRole.SendOnly
        ? "–"
        : retention switch
        {
            ServerRetention.KeepOnServer => l["Keep a copy"].Value,
            ServerRetention.DeleteAfterDownload => l["Delete after download"].Value,
            _ => l["Live access"].Value,
        };
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform;

public class AccountsModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PlatformListModel(db)
{
    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    public PageResult<MailAccount> Paged { get; private set; } = new(Array.Empty<MailAccount>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        IQueryable<MailAccount> query = Db.MailAccounts.IgnoreQueryFilters().AsNoTracking();
        if (TenantId is long tenantId)
        {
            query = query.Where(a => a.TenantId == tenantId);
        }

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

    public string RoleLabel(MailAccountRole role) => role switch
    {
        MailAccountRole.Mail => l["Mail"].Value,
        MailAccountRole.Backup => l["Backup"].Value,
        MailAccountRole.Migration => l["Migration"].Value,
        _ => l["Send only"].Value,
    };
}

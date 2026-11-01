using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Logs;

/// <summary>
/// The activity log of the tenant the person works in – only its own events, those of the installation are the platform's. The platform shows
/// the same list for every tenant (<c>Pages/Platform/Logs</c> overrides <see cref="Scope"/>); the markup is one partial, <c>_ActivityLogList</c>.
/// </summary>
public class IndexModel(LogListService logs, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
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

    /// <summary>On the platform: the tenant to show (empty: all of them, 0: the events of the installation itself).</summary>
    [BindProperty(SupportsGet = true)]
    public long? TenantId { get; set; }

    public PageResult<ActivityLog> Paged { get; private set; } = new(Array.Empty<ActivityLog>(), 0, 1, 1, LogListService.PageSize);

    /// <summary>Every tenant, for the column and the filter of the platform; empty in the administration of a tenant.</summary>
    public IReadOnlyList<Tenant> Tenants { get; private set; } = Array.Empty<Tenant>();

    /// <summary>The list shows every tenant (the platform), with a column and a filter for the tenant.</summary>
    public virtual bool AllTenants => false;

    /// <summary>Which entries the list shows: those of the tenant the person works in.</summary>
    protected virtual LogScope Scope => LogScope.OfTenant(currentUser.TenantId);

    public async Task OnGetAsync()
    {
        if (AllTenants)
        {
            Tenants = await logs.TenantsAsync();
        }

        Paged = await logs.ActivityAsync(new ActivityFilter(Search, Category, Level, From, To, Sort, PageNumber), Scope);
        PageNumber = Paged.PageNumber;
    }

    public string TenantName(long? tenantId) => tenantId is long id ? Tenants.FirstOrDefault(t => t.Id == id)?.Name ?? "?" : l["Installation"].Value;

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

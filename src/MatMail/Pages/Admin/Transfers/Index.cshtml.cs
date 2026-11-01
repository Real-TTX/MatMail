using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Transfers;

/// <summary>
/// The mail transfer log of the tenant the person works in: what came into the server, what went out and what moved between mailboxes, and how
/// it ended. Mail no tenant could be found for (refused mail for unknown domains) is the platform's, which shows the same list for every tenant
/// (<c>Pages/Platform/Transfers</c>); the markup is one partial, <c>_TransferList</c>.
/// </summary>
public class IndexModel(LogListService logs, CurrentUser currentUser, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Direction { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Channel { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "newest";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>On the platform: the tenant to show (empty: all of them, 0: what no tenant could be found for).</summary>
    [BindProperty(SupportsGet = true)]
    public long? TenantId { get; set; }

    public TransferLabels Labels { get; } = new(l);

    public PageResult<MailTransfer> Paged { get; private set; } = new(Array.Empty<MailTransfer>(), 0, 1, 1, LogListService.PageSize);

    /// <summary>Every tenant, for the column and the filter of the platform; empty in the administration of a tenant.</summary>
    public IReadOnlyList<Tenant> Tenants { get; private set; } = Array.Empty<Tenant>();

    /// <summary>The log is switched off (<c>Retention.TransferLogDays</c> is 0): the page says so instead of showing an empty list.</summary>
    public bool LogIsOff => config.Retention.TransferLogDays <= 0;

    public int KeptDays => config.Retention.TransferLogDays;

    public bool ShowsSubjects => config.Retention.TransferLogSubjects;

    /// <summary>The list shows every tenant (the platform), with a column and a filter for the tenant.</summary>
    public virtual bool AllTenants => false;

    /// <summary>Where a line opens: the details page of the same area.</summary>
    public virtual string DetailsPath => "/Admin/Transfers/Details";

    /// <summary>Which lines the list shows: those of the tenant the person works in.</summary>
    protected virtual LogScope Scope => LogScope.OfTenant(currentUser.TenantId);

    public async Task OnGetAsync()
    {
        if (AllTenants)
        {
            Tenants = await logs.TenantsAsync();
        }

        Paged = await logs.TransfersAsync(new TransferFilter(Search, Direction, Channel, Status, From, To, Sort, PageNumber), Scope);
        PageNumber = Paged.PageNumber;
    }

    public string TenantName(long? tenantId) => tenantId is long id ? Tenants.FirstOrDefault(t => t.Id == id)?.Name ?? "?" : l["Installation"].Value;
}

using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform.Administrators;

/// <summary>Who administers the platform. A platform administrator is a user of some tenant; this page only gives and takes the platform.</summary>
public class IndexModel(PlatformAdminService admins, PlatformListService lists, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    public IReadOnlyList<PlatformAdministrator> Rows { get; private set; } = Array.Empty<PlatformAdministrator>();

    public IReadOnlyList<MatMail.Data.Tenant> Tenants { get; private set; } = Array.Empty<MatMail.Data.Tenant>();

    public long? Self => currentUser.UserId;

    public async Task OnGetAsync()
    {
        Rows = await admins.ListAsync();
        Tenants = await lists.ListTenantsAsync(HttpContext.RequestAborted);
    }

    public string TenantName(long tenantId) => Tenants.FirstOrDefault(t => t.Id == tenantId)?.Name ?? "?";

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        string? error = await admins.RevokeAsync(id);
        if (error is not null)
        {
            this.Notify(l[error].Value, NoticeKind.Danger);
            return RedirectToPage();
        }

        this.Notify(l["The person no longer administers the platform."].Value);
        return id == currentUser.UserId ? Redirect("/") : RedirectToPage();   // whoever gave it up themselves has no platform any more
    }
}

using MatMail.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Platform;

/// <summary>The start page of the platform: every tenant with what is in it (<see cref="PlatformListService.TenantsAsync"/>).</summary>
public class IndexModel(PlatformListService lists) : PageModel
{
    public IReadOnlyList<PlatformTenantRow> Rows { get; private set; } = Array.Empty<PlatformTenantRow>();

    public async Task OnGetAsync() => Rows = await lists.TenantsAsync(HttpContext.RequestAborted);
}

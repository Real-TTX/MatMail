using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Platform;

/// <summary>
/// What the lists across all tenants have in common (system administrators only: the folder is closed to everybody else in
/// <c>Program.cs</c>): the toolbar (search, tenant, status, sort, page) and the tenant column. The rows come from
/// <see cref="PlatformListService"/> (no database in the pages). A row opens in its tenant: <see cref="OpenInTenant"/>.
/// </summary>
public abstract class PlatformListModel(PlatformListService lists) : PageModel
{
    protected PlatformListService Lists => lists;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? TenantId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Tenant> Tenants { get; private set; } = Array.Empty<Tenant>();

    /// <summary>The toolbar as the service takes it.</summary>
    protected PlatformListQuery Query => new(Search, TenantId, Status, Sort, PageNumber);

    protected async Task LoadTenantsAsync() => Tenants = await lists.ListTenantsAsync(HttpContext.RequestAborted);

    public string TenantName(long tenantId) => Tenants.FirstOrDefault(t => t.Id == tenantId)?.Name ?? "?";
}

/// <summary>
/// A button that switches the session into a tenant and then opens a page of its administration (a POST: a link must not change the
/// tenant a session works in). With <paramref name="Icon"/> it is an icon button, without it the title of the row.
/// </summary>
public sealed record OpenInTenant(long TenantId, string ReturnUrl, string Text, string? Icon = null)
{
    /// <summary>The icon of the button at the end of a row (a name that is a constant so that a test can look it up in the sprites).</summary>
    public const string OpenIcon = "arrow-right";
}

using MatMail.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Platform;

/// <summary>
/// What the lists across all tenants have in common (system administrators only: the folder is closed to everybody else in
/// <c>Program.cs</c>). They read with <c>IgnoreQueryFilters()</c>, which switches the tenant filter off for the whole query – exactly what
/// these lists are for – and never join a filtered set with an unfiltered one. A row opens in its tenant: <see cref="OpenInTenant"/>.
/// </summary>
public abstract class PlatformListModel(MatMailDbContext db) : PageModel
{
    protected MatMailDbContext Db => db;

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

    protected async Task LoadTenantsAsync() => Tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync();

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

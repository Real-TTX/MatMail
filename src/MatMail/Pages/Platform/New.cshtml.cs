using System.Security.Claims;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform;

/// <summary>
/// Something new from a list of the platform: a connected account, a mailbox, a domain or a user always belongs to one tenant, so this page asks
/// which, switches the session into it and opens the form of that tenant's administration (where the platform administrator then works with
/// the warning bar and the log of <see cref="PlatformAdminAuditMiddleware"/>).
/// </summary>
public class NewModel(PlatformListService lists, SignInService signIn, IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>What is made, and where its form is.</summary>
    public sealed record Kind(string Key, string Title, string Form, string List);

    [BindProperty(SupportsGet = true)]
    public string? What { get; set; }

    [BindProperty]
    public long? TenantId { get; set; }

    public Kind? Target { get; private set; }

    public IReadOnlyList<SelectListItem> TenantItems { get; private set; } = Array.Empty<SelectListItem>();

    private IReadOnlyList<Kind> Kinds =>
    [
        new("account", l["New connected account"].Value, "/Admin/Accounts/Edit", "/Platform/Accounts"),
        new("mailbox", l["New mailbox"].Value, "/Admin/Mailboxes/Edit", "/Platform/Mailboxes"),
        new("domain", l["New domain"].Value, "/Admin/Domains/Edit", "/Platform/Domains"),
        new("user", l["New user"].Value, "/Admin/Users/Edit", "/Platform/Users"),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (TenantId is not long tenantId || TenantItems.All(t => t.Value != tenantId.ToString()))
        {
            ModelState.AddModelError(nameof(TenantId), l["Choose the tenant it belongs to."]);
            return Page();
        }

        if (!Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token) || !await signIn.SwitchTenantAsync(token, tenantId))
        {
            ModelState.AddModelError(nameof(TenantId), l["The tenant could not be opened."]);
            return Page();
        }

        return LocalRedirect(Target!.Form);
    }

    private async Task<bool> LoadAsync()
    {
        Target = Kinds.FirstOrDefault(k => k.Key == What);
        if (Target is null)
        {
            return false;
        }

        TenantItems = (await lists.ListTenantsAsync(HttpContext.RequestAborted))
            .Where(t => t.IsActive)
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToList();
        return true;
    }
}

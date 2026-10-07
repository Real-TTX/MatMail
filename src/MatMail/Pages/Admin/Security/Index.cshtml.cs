using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Security;

/// <summary>The sign-in rules of the tenant: who has to use two-factor authentication.</summary>
public class IndexModel(TwoFactorPolicy policy, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public TwoFactorSummary Summary { get; private set; } = new(0, 0, 0);

    public IReadOnlyList<SelectListItem> ModeItems => new[]
    {
        new SelectListItem(l["Optional: every user decides for themselves"].Value, nameof(TwoFactorMode.Optional)),
        new SelectListItem(l["Required for administrators"].Value, nameof(TwoFactorMode.Administrators)),
        new SelectListItem(l["Required for everyone"].Value, nameof(TwoFactorMode.Everyone)),
    };

    public class InputModel
    {
        public TwoFactorMode Mode { get; set; }
    }

    public async Task OnGetAsync()
    {
        Input.Mode = await policy.GetModeAsync(TenantId, HttpContext.RequestAborted);
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || !Enum.IsDefined(Input.Mode))
        {
            ModelState.AddModelError(string.Empty, l["Choose one of the options."]);
            await LoadAsync();
            return Page();
        }

        string? error = await policy.SetModeAsync(TenantId, Input.Mode, HttpContext.RequestAborted);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, l[error]);
            await LoadAsync();
            return Page();
        }

        this.Notify(l["The security settings were saved."].Value);
        return RedirectToPage();
    }

    private long TenantId => currentUser.TenantId ?? 0;

    private async Task LoadAsync() => Summary = await policy.GetSummaryAsync(TenantId, HttpContext.RequestAborted);
}

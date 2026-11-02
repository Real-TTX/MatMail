using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform.Administrators;

/// <summary>Makes an existing user (of any tenant) a platform administrator.</summary>
public class AddModel(PlatformAdminService admins, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public string? LoginName { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        string? error = await admins.GrantAsync(LoginName);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(LoginName), l[error]);
            return Page();
        }

        this.Notify(l["The person now administers the platform."].Value);
        return RedirectToPage("Index");
    }
}

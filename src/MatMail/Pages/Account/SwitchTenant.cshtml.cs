using System.Security.Claims;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Account;

/// <summary>Changes the tenant a session works in. Who may enter which tenant decides <see cref="SignInService.SwitchTenantAsync"/> alone.</summary>
public class SwitchTenantModel(SignInService signIn) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync(long tenantId, string? returnUrl)
    {
        if (Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token))
        {
            await signIn.SwitchTenantAsync(token, tenantId);
        }

        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}

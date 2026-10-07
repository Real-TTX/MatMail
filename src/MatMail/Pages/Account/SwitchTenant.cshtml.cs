using System.Security.Claims;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Account;

/// <summary>System administrators change the tenant they are working in.</summary>
public class SwitchTenantModel(SignInService signIn, CurrentUser currentUser) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync(long tenantId, string? returnUrl)
    {
        if (currentUser.IsSystemAdmin && Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token))
        {
            await signIn.SwitchTenantAsync(token, tenantId);
        }

        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}

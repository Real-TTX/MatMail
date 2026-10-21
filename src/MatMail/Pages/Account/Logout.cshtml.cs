using MatMail.Services;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Account;

/// <summary>Ends the session. After a sign-in through MatIdentity the session there ends too (and the browser comes back to the sign-in page with <c>?signedOut=1</c>).</summary>
public class LogoutModel(SignInService signIn, MatPlatformSettings platform) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        string? identityToken = null;
        MatIdentityPendingLink.Clear(HttpContext);
        if (User.Identity?.IsAuthenticated == true)
        {
            identityToken = await signIn.SignOutAsync();
        }

        if (platform.IdentityEnabled && !string.IsNullOrEmpty(identityToken))
        {
            // Then at MatIdentity as well; it sends the browser back to /signout-callback-oidc and from there to the sign-in page.
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, identityToken);
        }

        // With MatIdentity configured the sign-in page would otherwise go straight back there and sign the person in again.
        return platform.IdentityEnabled ? Redirect("/Account/Login?signedOut=1") : RedirectToPage("/Account/Login");
    }
}

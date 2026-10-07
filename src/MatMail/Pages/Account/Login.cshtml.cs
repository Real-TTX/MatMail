using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class LoginModel(SignInService signIn, TwoFactorTicket ticket, BrandingService branding, IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>The name of a tenant (from /t/name): its logo and colour are shown on the page.</summary>
    [BindProperty(SupportsGet = true, Name = "t")]
    public string? TenantName { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        public string LoginName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool Remember { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl());
        }

        await ApplyBrandAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SignInOutcome result = await signIn.ValidateCredentialsAsync(Input.LoginName, Input.Password, HttpContext.ClientAddress());
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Status == SignInStatus.LockedOut
                ? l[SignInService.LockedMessage]
                : l["The login name or the password is wrong."]);
            Input.Password = string.Empty;
            await ApplyBrandAsync();
            return Page();
        }

        if (result.SecondFactorPending)
        {
            // The password is right. The session only comes into being after the code: until then a ticket (five minutes, readable
            // by the server only) is all the browser holds.
            ticket.Issue(HttpContext, new PendingSignIn(result.User!.Id, Input.Remember, SafeReturnUrl(), TenantName));
            return RedirectToPage("/Account/TwoFactor");
        }

        await signIn.SignInAsync(result.User!, Input.Remember);

        // Bound to two-factor authentication without having set it up: straight to the set-up (the rest of the site is closed to them anyway).
        return result.TwoFactor is { SetupRequired: true } ? Redirect(TwoFactorSetupMiddleware.SecurityPath) : LocalRedirect(SafeReturnUrl());
    }

    private async Task ApplyBrandAsync()
    {
        Brand? brand = await branding.FindBySlugAsync(TenantName, HttpContext.RequestAborted);
        if (brand is not null)
        {
            ViewData["Brand"] = brand;
        }
    }

    private string SafeReturnUrl() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}

using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

/// <summary>
/// The second step of the sign-in: after a right password, the code of the authenticator app (or a recovery code). The page opens
/// with the ticket the password page handed out; without it there is nothing to do here.
/// </summary>
public class TwoFactorModel(TwoFactorService twoFactor, TwoFactorTicket ticket, SignInService signIn, BrandingService branding, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Where "Back" leads: the sign-in page the person came from.</summary>
    public string BackUrl { get; private set; } = "/Account/Login";

    public class InputModel
    {
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect("/");
        }

        PendingSignIn? pending = ticket.Read(HttpContext);
        if (pending is null)
        {
            return StartOver();
        }

        await ApplyBrandAsync(pending);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        PendingSignIn? pending = ticket.Read(HttpContext);
        if (pending is null)
        {
            return StartOver();
        }

        SecondStepOutcome outcome = await twoFactor.VerifyLoginAsync(pending.UserId, Input.Code, HttpContext.ClientAddress(), HttpContext.RequestAborted);
        if (outcome.User is null)
        {
            // A refused code is not kept in the field.
            ModelState.Remove("Input.Code");
            Input.Code = string.Empty;
            ModelState.AddModelError(string.Empty, outcome.Result.Status == SecondFactorStatus.LockedOut
                ? l[SignInService.LockedMessage]
                : l[TwoFactorService.WrongCodeMessage]);
            await ApplyBrandAsync(pending);
            return Page();
        }

        ticket.Clear(HttpContext);
        await signIn.SignInAsync(outcome.User, pending.Remember);

        if (outcome.Result.UsedRecoveryCode)
        {
            this.Notify(
                outcome.Result.RecoveryCodesLeft == 0
                    ? l["You signed in with your last recovery code. Create new ones under Security in your account."].Value
                    : string.Format(l["You signed in with a recovery code. Recovery codes left: {0}. You can create new ones under Security in your account."].Value, outcome.Result.RecoveryCodesLeft),
                NoticeKind.Warn);
        }

        return LocalRedirect(!string.IsNullOrEmpty(pending.ReturnUrl) && Url.IsLocalUrl(pending.ReturnUrl) ? pending.ReturnUrl : "/");
    }

    /// <summary>No ticket (or an old one): the password has to be entered again.</summary>
    private IActionResult StartOver()
    {
        this.Notify(l["The sign-in took too long. Please start again."].Value, NoticeKind.Warn);
        return RedirectToPage("/Account/Login");
    }

    private async Task ApplyBrandAsync(PendingSignIn pending)
    {
        Brand? brand = await branding.FindBySlugAsync(pending.TenantSlug, HttpContext.RequestAborted);
        if (brand is not null)
        {
            ViewData["Brand"] = brand;
            BackUrl = "/Account/Login?t=" + Uri.EscapeDataString(pending.TenantSlug!);
        }
    }
}

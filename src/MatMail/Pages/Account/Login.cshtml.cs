using MatMail.Platform;
using MatMail.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

/// <summary>
/// The web sign-in: login name or address and password (then the code, when two-factor authentication is on). With MatIdentity configured
/// (<c>MATIDENTITY_AUTHORITY</c>) a visit goes straight on to MatIdentity; the local form stays reachable with <c>?local=1</c> (the way in for an
/// administrator when MatIdentity is down), <c>?signedOut=1</c> (back from the sign-out there) and <c>?ssoError=1</c> (it failed) do not go on.
/// <c>?link=1</c>: a person of MatIdentity has the address of an account here (<see cref="MatIdentityPendingLink"/>); the sign-in of exactly that
/// account (with its code, when it has a second factor) links it, another account links nothing, "Cancel" forgets the link.
/// </summary>
public class LoginModel(
    SignInService signIn,
    MailIdentityUsers identityUsers,
    TwoFactorTicket ticket,
    BrandingService branding,
    MatPlatformSettings platform,
    IStringLocalizer<SharedResource> l,
    ILogger<LoginModel> logger) : PageModel
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

    /// <summary>The sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => platform.IdentityEnabled;

    /// <summary><c>?local=1</c>: the local form, also when MatIdentity is configured.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed, or MatIdentity cannot be reached.</summary>
    public bool SsoError { get; private set; }

    /// <summary>A person of MatIdentity waits for an account here to be linked by its own sign-in (null: none, or it expired).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: the ten minutes are over.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>The local form is shown: always without MatIdentity, with it only on <c>?local=1</c>.</summary>
    public bool ShowLocalForm => !IdentityEnabled || Local;

    /// <summary>Starts the sign-in at MatIdentity again, back to <see cref="ReturnUrl"/> afterwards.</summary>
    public string IdentitySignInUrl => WithReturnUrl("/Account/Login");

    /// <summary>The local form, with the same <see cref="ReturnUrl"/>.</summary>
    public string LocalSignInUrl => WithReturnUrl("/Account/Login?local=1");

    public async Task<IActionResult> OnGetAsync()
    {
        ReadFlags();
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl());
        }

        // Back from MatIdentity with the code still to come (two-factor authentication is on for this user).
        if (ticket.Read(HttpContext) is { IdentityToken: not null })
        {
            return RedirectToPage("/Account/TwoFactor");
        }

        // Automatic sign-in: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !QueryFlag("signedOut") && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeReturnUrl() });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // Its configuration cannot be read: MatIdentity is not reachable, the local form is the way in.
                logger.LogWarning(ex, "MatIdentity is not reachable, the local sign-in form is shown");
                Local = true;
                SsoError = true;
            }
        }

        await ApplyBrandAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // A posted form is the local sign-in; after an error it shows the form again.
        ReadFlags();
        Local = true;

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

        // A pending link of MatIdentity counts for exactly the account that matched; it is gone either way.
        string? identityToken = await identityUsers.LinkPendingAsync(HttpContext, result.User!, HttpContext.RequestAborted);
        await signIn.SignInAsync(result.User!, Input.Remember, identityToken);

        // Bound to two-factor authentication without having set it up: straight to the set-up (the rest of the site is closed to them anyway).
        return result.TwoFactor is { SetupRequired: true } ? Redirect(TwoFactorSetupMiddleware.SecurityPath) : LocalRedirect(SafeReturnUrl());
    }

    /// <summary>"Cancel" on <c>?link=1</c>: the account is not linked, the pending link is forgotten.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect(WithReturnUrl("/Account/Login?local=1"));
    }

    private void ReadFlags()
    {
        Local = QueryFlag("local");
        SsoError = QueryFlag("ssoError");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    private async Task ApplyBrandAsync()
    {
        Brand? brand = await branding.FindBySlugAsync(TenantName, HttpContext.RequestAborted);
        if (brand is not null)
        {
            ViewData["Brand"] = brand;
        }
    }

    private string WithReturnUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl))
        {
            return path;
        }

        return path + (path.Contains('?') ? "&" : "?") + "returnUrl=" + Uri.EscapeDataString(ReturnUrl);
    }

    private string SafeReturnUrl() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}

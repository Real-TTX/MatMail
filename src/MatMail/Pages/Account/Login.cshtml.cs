using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class LoginModel(SignInService signIn, IStringLocalizer<SharedResource> l) : PageModel
{
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

    public IActionResult OnGet()
        => User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        SignInOutcome result = await signIn.ValidateCredentialsAsync(Input.LoginName, Input.Password, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Status == SignInStatus.LockedOut
                ? l["Too many failed attempts. Please try again in a few minutes."]
                : l["The login name or the password is wrong."]);
            Input.Password = string.Empty;
            return Page();
        }

        await signIn.SignInAsync(result.User!, Input.Remember);
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}

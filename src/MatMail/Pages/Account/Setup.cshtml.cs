using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class SetupModel(SetupService setup, SignInService signIn, MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new() { TenantName = "Home" };

    public class InputModel
    {
        public string TenantName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public string? MailAddress { get; set; }
        public string Password { get; set; } = string.Empty;
        public string PasswordRepeat { get; set; } = string.Empty;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.TenantName))
        {
            ModelState.AddModelError("Input.TenantName", l["Name is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.DisplayName))
        {
            ModelState.AddModelError("Input.DisplayName", l["Name is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.LoginName))
        {
            ModelState.AddModelError("Input.LoginName", l["Login name is required."]);
        }

        if (!string.IsNullOrWhiteSpace(Input.MailAddress) && !MailAddresses.IsValid(Input.MailAddress))
        {
            ModelState.AddModelError("Input.MailAddress", l["The e-mail address is not valid."]);
        }

        string? passwordError = SignInService.ValidatePasswordStrength(Input.Password);
        if (passwordError is not null)
        {
            ModelState.AddModelError("Input.Password", l[passwordError]);
        }
        else if (Input.Password != Input.PasswordRepeat)
        {
            ModelState.AddModelError("Input.PasswordRepeat", l["The passwords do not match."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        string? error = await setup.CreateFirstAdministratorAsync(Input.TenantName, Input.DisplayName, Input.LoginName, Input.MailAddress, Input.Password);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, l[error]);
            return Page();
        }

        User user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.LoginName == SignInService.NormalizeLoginName(Input.LoginName));
        await signIn.SignInAsync(user);
        return Redirect("/");
    }
}

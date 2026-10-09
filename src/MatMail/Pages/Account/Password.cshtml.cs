using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class PasswordModel(MatMailDbContext db, SignInService signIn, UserService users, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The directory (Active Directory, LDAP) that signs this user in: then the password is changed there, not here.</summary>
    public string? DirectoryName { get; private set; }

    public class InputModel
    {
        public string Current { get; set; } = string.Empty;
        public string New { get; set; } = string.Empty;
        public string Repeat { get; set; } = string.Empty;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (DirectoryName is not null)
        {
            return Page();
        }

        string? loginName = currentUser.Username;
        SignInOutcome check = await signIn.ValidateCredentialsAsync(loginName, Input.Current, HttpContext.ClientAddress());
        if (!check.Succeeded)
        {
            ModelState.AddModelError("Input.Current", l["The current password is wrong."]);
        }

        string? strength = SignInService.ValidatePasswordStrength(Input.New);
        if (strength is not null)
        {
            ModelState.AddModelError("Input.New", l[strength]);
        }
        else if (Input.New != Input.Repeat)
        {
            ModelState.AddModelError("Input.Repeat", l["The passwords do not match."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token);
        string? error = await users.ChangePasswordAsync(currentUser.UserId!.Value, Input.New, token);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, l[error]);
            return Page();
        }

        this.Notify(l["Your password was changed."].Value);
        return Redirect("/Account/Password");
    }

    private async Task LoadAsync()
        => DirectoryName = await db.Users.AsNoTracking().Where(u => u.Id == currentUser.UserId && u.DirectoryId != null).Select(u => u.DirectoryConnection!.Name).FirstOrDefaultAsync();
}

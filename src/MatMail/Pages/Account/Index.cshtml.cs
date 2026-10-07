using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class IndexModel(MatMailDbContext db, CurrentUser currentUser, SessionCache cache, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string LoginName { get; private set; } = string.Empty;
    public string TenantName { get; private set; } = string.Empty;

    public class InputModel
    {
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? JobTitle { get; set; }
        public string? Phone { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        User? user = await LoadUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        Input = new InputModel { DisplayName = user.DisplayName, Email = user.Email, JobTitle = user.JobTitle, Phone = user.Phone };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        User? user = await LoadUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Input.DisplayName))
        {
            ModelState.AddModelError("Input.DisplayName", l["Name is required."]);
        }

        if (!string.IsNullOrWhiteSpace(Input.Email) && !MailAddresses.IsValid(Input.Email))
        {
            ModelState.AddModelError("Input.Email", l["The e-mail address is not valid."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        user.DisplayName = Input.DisplayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim();
        user.JobTitle = string.IsNullOrWhiteSpace(Input.JobTitle) ? null : Input.JobTitle.Trim();
        user.Phone = string.IsNullOrWhiteSpace(Input.Phone) ? null : Input.Phone.Trim();
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);

        this.Notify(l["Your profile was saved."].Value);
        return RedirectToPage();
    }

    private async Task<User?> LoadUserAsync()
    {
        long? id = currentUser.UserId;
        User? user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id);
        if (user is not null)
        {
            LoginName = user.LoginName;
            TenantName = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Name).FirstOrDefaultAsync() ?? string.Empty;
        }

        return user;
    }
}

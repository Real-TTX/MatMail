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

    public class InputModel : IPersonFields
    {
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? JobTitle { get; set; }
        public string? Phone { get; set; }
        public string? Salutation { get; set; }
        public string? Title { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Department { get; set; }
        public string? Mobile { get; set; }
        public string? Fax { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        User? user = await LoadUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            DisplayName = user.DisplayName, Email = user.Email, JobTitle = user.JobTitle, Phone = user.Phone,
            Salutation = user.Salutation,
            Title = user.Title,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Department = user.Department,
            Mobile = user.Mobile,
            Fax = user.Fax,
        };
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
        user.Salutation = string.IsNullOrWhiteSpace(Input.Salutation) ? null : Input.Salutation.Trim();
        user.Title = string.IsNullOrWhiteSpace(Input.Title) ? null : Input.Title.Trim();
        user.FirstName = string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim();
        user.LastName = string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim();
        user.Department = string.IsNullOrWhiteSpace(Input.Department) ? null : Input.Department.Trim();
        user.Mobile = string.IsNullOrWhiteSpace(Input.Mobile) ? null : Input.Mobile.Trim();
        user.Fax = string.IsNullOrWhiteSpace(Input.Fax) ? null : Input.Fax.Trim();
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

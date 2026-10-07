using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Users;

public class EditModel(MatMailDbContext db, UserService users, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> RoleItems { get; private set; } = Array.Empty<SelectListItem>();
    public Mailbox? Mailbox { get; private set; }
    public IReadOnlyList<string> MailboxAddresses { get; private set; } = Array.Empty<string>();

    public bool IsEdit => Id != 0;
    public bool IsSelf => Id == currentUser.UserId;
    public bool CanSetSystemAdmin => currentUser.IsSystemAdmin;

    public class InputModel : IPersonFields
    {
        public string LoginName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? JobTitle { get; set; }
        public string? Salutation { get; set; }
        public string? Title { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Department { get; set; }
        public string? Mobile { get; set; }
        public string? Fax { get; set; }
        public string? Password { get; set; }
        public bool MustChangePassword { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsSystemAdmin { get; set; }
        public long[] RoleIds { get; set; } = Array.Empty<long>();
        public bool CreateMailbox { get; set; } = true;
        public string? PrimaryAddress { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            long userRoleId = RoleItems.Where(r => r.Text == TenantService.UserRoleName).Select(r => long.Parse(r.Value)).FirstOrDefault();
            Input.RoleIds = userRoleId == 0 ? Array.Empty<long>() : new[] { userRoleId };
            return Page();
        }

        User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Id);
        if (user is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            LoginName = user.LoginName,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Phone = user.Phone,
            JobTitle = user.JobTitle,
            Salutation = user.Salutation,
            Title = user.Title,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Department = user.Department,
            Mobile = user.Mobile,
            Fax = user.Fax,
            MustChangePassword = user.MustChangePassword,
            IsActive = user.IsActive,
            IsSystemAdmin = user.IsSystemAdmin,
            RoleIds = await db.UserRoles.Where(ur => ur.UserId == Id).Select(ur => ur.RoleId).ToArrayAsync(),
            CreateMailbox = false,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();

        var userInput = new UserInput
        {
            LoginName = Input.LoginName,
            DisplayName = Input.DisplayName,
            Email = Input.Email,
            Phone = Input.Phone,
            JobTitle = Input.JobTitle,
            Salutation = Input.Salutation,
            Title = Input.Title,
            FirstName = Input.FirstName,
            LastName = Input.LastName,
            Department = Input.Department,
            Mobile = Input.Mobile,
            Fax = Input.Fax,
            Password = Input.Password,
            MustChangePassword = Input.MustChangePassword,
            IsActive = Input.IsActive,
            IsSystemAdmin = Input.IsSystemAdmin && currentUser.IsSystemAdmin,
            RoleIds = Input.RoleIds ?? Array.Empty<long>(),
            CreateMailbox = !IsEdit && Input.CreateMailbox,
            PrimaryAddress = Input.PrimaryAddress,
        };

        if (IsEdit)
        {
            // The system administrator flag is only editable by system administrators; others keep the stored value.
            if (!currentUser.IsSystemAdmin)
            {
                userInput.IsSystemAdmin = await db.Users.Where(u => u.Id == Id).Select(u => u.IsSystemAdmin).FirstOrDefaultAsync();
            }

            Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid ownSession);
            string? error = await users.UpdateAsync(Id, userInput, ownSession == Guid.Empty ? null : ownSession);
            if (error is not null)
            {
                ModelState.AddModelError(string.Empty, l[error]);
                return Page();
            }

            if (Input.CreateMailbox && await db.Mailboxes.AllAsync(m => m.OwnerUserId != Id || m.Type != MailboxType.Personal))
            {
                string? mailboxError = await CreateMailboxForExistingUserAsync();
                if (mailboxError is not null)
                {
                    ModelState.AddModelError("Input.PrimaryAddress", l[mailboxError]);
                    return Page();
                }
            }

            this.Notify(l["The user was saved."].Value);
            return RedirectToPage("Index");
        }

        (User? user, string? createError) = await users.CreateAsync(userInput);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, l[createError!]);
            return Page();
        }

        if (createError is not null)
        {
            // The user exists, only the address could not be added: continue on the edit page.
            this.Notify(l[createError].Value, NoticeKind.Warn);
            return RedirectToPage(new { user.Id });
        }

        this.Notify(l["The user was created."].Value);
        return RedirectToPage("Index");
    }

    public Task<IActionResult> OnPostDeleteAsync() => DeleteAsync(deleteMailbox: false);

    public Task<IActionResult> OnPostDeleteWithMailboxAsync() => DeleteAsync(deleteMailbox: true);

    private async Task<IActionResult> DeleteAsync(bool deleteMailbox)
    {
        string? error = await users.DeleteAsync(Id, deleteMailbox);
        if (error is not null)
        {
            this.Notify(l[error].Value, NoticeKind.Danger);
            return RedirectToPage(new { Id });
        }

        this.Notify(l["The user was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task<string?> CreateMailboxForExistingUserAsync()
    {
        User user = await db.Users.FirstAsync(u => u.Id == Id);
        var mailboxes = HttpContext.RequestServices.GetRequiredService<MailboxService>();
        Mailbox mailbox = await mailboxes.CreateMailboxAsync(user.DisplayName, MailboxType.Personal, user.Id, user.TenantId);
        string? address = string.IsNullOrWhiteSpace(Input.PrimaryAddress) ? null : Input.PrimaryAddress;
        return address is null ? null : await mailboxes.AddAddressAsync(mailbox, address, isPrimary: true, registerDomain: currentUser.Can(Permissions.DomainsManage));
    }

    private async Task LoadListsAsync()
    {
        RoleItems = await db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToListAsync();
        if (!IsEdit)
        {
            return;
        }

        Mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.OwnerUserId == Id && m.Type == MailboxType.Personal);
        if (Mailbox is not null)
        {
            MailboxAddresses = await db.MailboxAliases.AsNoTracking().Where(a => a.MailboxId == Mailbox.Id)
                .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address).Select(a => a.Address).ToListAsync();
        }
    }
}

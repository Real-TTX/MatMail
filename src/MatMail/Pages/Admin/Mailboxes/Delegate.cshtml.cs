using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Mailboxes;

public class DelegateModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long MailboxId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public string UserName { get; private set; } = string.Empty;
    public IReadOnlyList<SelectListItem> UserItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> AccessItems => new[]
    {
        new SelectListItem(l["Read"].Value, nameof(MailboxAccess.Read)),
        new SelectListItem(l["Read and edit"].Value, nameof(MailboxAccess.Edit)),
        new SelectListItem(l["Read, edit and send"].Value, nameof(MailboxAccess.Send)),
        new SelectListItem(l["Full control"].Value, nameof(MailboxAccess.Manage)),
    };

    public class InputModel
    {
        public long? UserId { get; set; }
        public string Access { get; set; } = nameof(MailboxAccess.Send);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Mailbox? mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == MailboxId && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        await LoadListsAsync(mailbox);
        if (!IsEdit)
        {
            return Page();
        }

        MailboxPermission? permission = await db.MailboxPermissions.AsNoTracking().Include(p => p.User).FirstOrDefaultAsync(p => p.Id == Id && p.MailboxId == MailboxId);
        if (permission is null)
        {
            return NotFound();
        }

        UserName = permission.User!.DisplayName;
        Input = new InputModel { UserId = permission.UserId, Access = permission.Access.ToString() };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Mailbox? mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == MailboxId && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        await LoadListsAsync(mailbox);
        if (!Enum.TryParse(Input.Access, out MailboxAccess access))
        {
            access = MailboxAccess.Read;
        }

        MailboxPermission? permission = null;
        if (IsEdit)
        {
            permission = await db.MailboxPermissions.Include(p => p.User).FirstOrDefaultAsync(p => p.Id == Id && p.MailboxId == MailboxId);
            if (permission is null)
            {
                return NotFound();
            }

            UserName = permission.User!.DisplayName;
        }
        else if (Input.UserId is not long userId)
        {
            ModelState.AddModelError("Input.UserId", l["Select a user."]);
        }
        else if (userId == mailbox.OwnerUserId)
        {
            ModelState.AddModelError("Input.UserId", l["The owner already has full access."]);
        }
        else if (await db.MailboxPermissions.AnyAsync(p => p.MailboxId == MailboxId && p.UserId == userId))
        {
            ModelState.AddModelError("Input.UserId", l["This user already has access."]);
        }
        else if (!UserItems.Any(u => u.Value == userId.ToString()))
        {
            ModelState.AddModelError("Input.UserId", l["The user does not exist."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (permission is null)
        {
            db.MailboxPermissions.Add(new MailboxPermission { TenantId = mailbox.TenantId, MailboxId = MailboxId, UserId = Input.UserId!.Value, Access = access });
        }
        else
        {
            permission.Access = access;
        }

        await db.SaveChangesAsync();
        this.Notify(l["The access was saved."].Value);
        return RedirectToPage("Access", new { Id = MailboxId });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.MailboxPermissions.Where(p => p.Id == Id && p.MailboxId == MailboxId).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The access was removed."].Value);
        return RedirectToPage("Access", new { Id = MailboxId });
    }

    private async Task LoadListsAsync(Mailbox mailbox)
    {
        UserItems = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Id != mailbox.OwnerUserId && !db.MailboxPermissions.Any(p => p.MailboxId == mailbox.Id && p.UserId == u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new SelectListItem(u.DisplayName + " (" + u.LoginName + ")", u.Id.ToString()))
            .ToListAsync();
    }
}

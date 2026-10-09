using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Mailboxes;

public class EditModel(MatMailDbContext db, MailboxService mailboxes, MailboxUsageService usageService, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;

    /// <summary>What the mailbox holds (existing mailboxes only) and where.</summary>
    public MailboxUsage Usage { get; private set; } = MailboxUsage.Empty;
    public IReadOnlyList<FolderUsage> FolderUsages { get; private set; } = Array.Empty<FolderUsage>();
    public string TypeText { get; private set; } = string.Empty;
    public IReadOnlyList<SelectListItem> UserItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> TypeItems => new[]
    {
        new SelectListItem(l["Personal mailbox of a user"].Value, nameof(MailboxType.Personal)),
        new SelectListItem(l["Shared mailbox (public folder)"].Value, nameof(MailboxType.Shared)),
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = nameof(MailboxType.Personal);
        public long? OwnerUserId { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        Mailbox? mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = mailbox.Name,
            Type = mailbox.Type.ToString(),
            OwnerUserId = mailbox.OwnerUserId,
            Description = mailbox.Description,
            IsActive = mailbox.IsActive,
        };
        TypeText = TypeLabel(mailbox.Type);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        string name = Input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }

        Mailbox? mailbox = null;
        if (IsEdit)
        {
            mailbox = await db.Mailboxes.FirstOrDefaultAsync(m => m.Id == Id && m.Type != MailboxType.Unassigned);
            if (mailbox is null)
            {
                return NotFound();
            }

            TypeText = TypeLabel(mailbox.Type);
        }

        MailboxType type = mailbox?.Type ?? (Input.Type == nameof(MailboxType.Shared) ? MailboxType.Shared : MailboxType.Personal);
        long? ownerId = type == MailboxType.Personal ? Input.OwnerUserId : null;
        if (type == MailboxType.Personal && ownerId is null)
        {
            ModelState.AddModelError("Input.OwnerUserId", l["A personal mailbox needs an owner."]);
        }
        else if (ownerId is long owner && await db.Mailboxes.AnyAsync(m => m.Id != Id && m.Type == MailboxType.Personal && m.OwnerUserId == owner))
        {
            ModelState.AddModelError("Input.OwnerUserId", l["This user already has a personal mailbox."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (mailbox is null)
        {
            Mailbox created = await mailboxes.CreateMailboxAsync(name, type, ownerId);
            created.Description = Clean(Input.Description);
            created.IsActive = Input.IsActive;
            await db.SaveChangesAsync();

            this.Notify(l["The mailbox was created. Add its addresses now."].Value);
            return RedirectToPage("Addresses", new { created.Id });
        }

        mailbox.Name = name;
        mailbox.OwnerUserId = ownerId;
        mailbox.Description = Clean(Input.Description);
        mailbox.IsActive = Input.IsActive;
        await db.SaveChangesAsync();

        this.Notify(l["The mailbox was saved."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        // Folders, messages, addresses and delegations go with it (cascading deletes in the database).
        int deleted = await db.Mailboxes.Where(m => m.Id == Id && m.Type != MailboxType.Unassigned).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The mailbox was deleted."].Value);
        return RedirectToPage("Index");
    }

    private string TypeLabel(MailboxType type) => type == MailboxType.Shared ? l["Shared mailbox (public folder)"].Value : l["Personal mailbox of a user"].Value;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task LoadListsAsync()
    {
        UserItems = await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new SelectListItem(u.DisplayName + " (" + u.LoginName + ")", u.Id.ToString()))
            .ToListAsync();

        if (IsEdit)
        {
            Usage = await usageService.GetAsync(Id);
            FolderUsages = await usageService.GetFoldersAsync(Id);
        }
    }
}

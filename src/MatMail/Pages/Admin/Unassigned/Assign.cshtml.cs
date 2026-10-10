using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Unassigned;

public class AssignModel(MatMailDbContext db, MailStore store, FolderService folders, MailboxService mailboxes, MailboxQuotaService quota, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public MailMessage? Message { get; private set; }
    public IReadOnlyList<string> Addresses { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public long? MailboxId { get; set; }
        public bool AddAddresses { get; set; } = true;
        public bool MoveSimilar { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        return await LoadAsync() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Mailbox? target = Input.MailboxId is long id ? await db.Mailboxes.FirstOrDefaultAsync(m => m.Id == id && m.Type != MailboxType.Unassigned && m.IsActive) : null;
        if (target is null)
        {
            ModelState.AddModelError("Input.MailboxId", l["Select a mailbox."]);
            return Page();
        }

        if (await quota.IsFullAsync(target))
        {
            ModelState.AddModelError("Input.MailboxId", l["This mailbox is full: it takes no new mail until something is deleted."]);
            return Page();
        }

        MailFolder inbox = (await folders.FindByKindAsync(target.Id, FolderKind.Inbox))!;
        var toMove = new List<long> { Id };

        if (Input.AddAddresses)
        {
            foreach (string address in Addresses)
            {
                // Only addresses of registered domains can be claimed; one that already belongs to someone is left alone.
                await mailboxes.AddAddressAsync(target, address, isPrimary: false);
            }

            if (Input.MoveSimilar)
            {
                List<MailMessage> candidates = await db.MailMessages
                    .Where(m => m.Id != Id && m.Folder!.Mailbox!.Type == MailboxType.Unassigned && m.EnvelopeRecipients != null)
                    .Take(500).ToListAsync();
                toMove.AddRange(candidates
                    .Where(m => Addresses.Any(a => m.EnvelopeRecipients!.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    .Select(m => m.Id));
            }
        }

        IReadOnlyList<MailMessage> moved = await store.MoveAsync(toMove, inbox.Id);
        this.Notify(string.Format(l["{0} message(s) were handed over to “{1}”."].Value, moved.Count, target.Name));
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        await store.DeleteAsync(new[] { Id }, permanent: true);
        this.Notify(l["The message was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync()
    {
        Message = await db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id && m.Folder!.Mailbox!.Type == MailboxType.Unassigned);
        if (Message is null)
        {
            return false;
        }

        Addresses = (Message.EnvelopeRecipients ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MailAddresses.Normalize).Where(MailAddresses.IsValid).Distinct().ToList();
        MailboxItems = await db.Mailboxes.AsNoTracking().Where(m => m.Type != MailboxType.Unassigned && m.IsActive).OrderBy(m => m.Name)
            .Select(m => new SelectListItem(m.Name, m.Id.ToString())).ToListAsync();
        return true;
    }
}

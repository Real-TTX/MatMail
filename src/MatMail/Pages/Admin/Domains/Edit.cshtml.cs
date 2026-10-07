using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Domains;

public class EditModel(MatMailDbContext db, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public int AddressCount { get; private set; }
    public string Hostname => config.Server.Hostname;
    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public long? CatchAllMailboxId { get; set; }
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        Domain? domain = await db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == Id);
        if (domain is null)
        {
            return NotFound();
        }

        Input = new InputModel { Name = domain.Name, IsActive = domain.IsActive, CatchAllMailboxId = domain.CatchAllMailboxId, Notes = domain.Notes };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        string name = MailAddresses.Normalize(Input.Name);

        Domain? domain = null;
        if (IsEdit)
        {
            domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == Id);
            if (domain is null)
            {
                return NotFound();
            }
        }

        // The name of a domain that already has addresses is fixed.
        if (domain is not null && AddressCount > 0)
        {
            name = domain.Name;
        }

        if (!MailAddresses.IsValidDomain(name))
        {
            ModelState.AddModelError("Input.Name", l["This is not a valid domain name."]);
        }
        else if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Name == name && d.Id != Id))
        {
            ModelState.AddModelError("Input.Name", l["This domain is already registered."]);
        }

        if (Input.CatchAllMailboxId is long mailboxId && !MailboxItems.Any(m => m.Value == mailboxId.ToString()))
        {
            ModelState.AddModelError("Input.CatchAllMailboxId", l["The mailbox does not exist."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (domain is null)
        {
            domain = new Domain();
            db.Domains.Add(domain);
        }

        domain.Name = name;
        domain.IsActive = Input.IsActive;
        domain.CatchAllMailboxId = Input.CatchAllMailboxId;
        domain.Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();
        await db.SaveChangesAsync();

        this.Notify(l[IsEdit ? "The domain was saved." : "The domain was created."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        Domain? domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == Id);
        if (domain is null)
        {
            return NotFound();
        }

        if (await db.MailboxAliases.AnyAsync(a => a.Address.EndsWith("@" + domain.Name)))
        {
            this.Notify(l["Remove the addresses of this domain first."].Value, NoticeKind.Danger);
            return RedirectToPage(new { Id });
        }

        db.Domains.Remove(domain);
        await db.SaveChangesAsync();
        this.Notify(l["The domain was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        MailboxItems = await db.Mailboxes.AsNoTracking()
            .Where(m => m.Type != MailboxType.Unassigned)
            .OrderBy(m => m.Name)
            .Select(m => new SelectListItem(m.Name, m.Id.ToString()))
            .ToListAsync();

        if (IsEdit)
        {
            string? name = await db.Domains.Where(d => d.Id == Id).Select(d => d.Name).FirstOrDefaultAsync();
            AddressCount = name is null ? 0 : await db.MailboxAliases.CountAsync(a => a.Address.EndsWith("@" + name));
        }
    }
}

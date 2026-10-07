using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Mailboxes;

public class AddressesModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public string MailboxName { get; private set; } = string.Empty;
    public IReadOnlyList<MailboxAlias> Aliases { get; private set; } = Array.Empty<MailboxAlias>();

    public async Task<IActionResult> OnGetAsync()
    {
        Mailbox? mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        MailboxName = mailbox.Name;
        Aliases = await db.MailboxAliases.AsNoTracking().Include(a => a.SendAccount)
            .Where(a => a.MailboxId == Id)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address)
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMakePrimaryAsync(long aliasId)
    {
        List<MailboxAlias> aliases = await db.MailboxAliases.Where(a => a.MailboxId == Id).ToListAsync();
        MailboxAlias? target = aliases.FirstOrDefault(a => a.Id == aliasId);
        if (target is null || target.IsCatchAll)
        {
            return NotFound();
        }

        aliases.ForEach(a => a.IsPrimary = a.Id == aliasId);
        await db.SaveChangesAsync();
        this.Notify(l["The primary address was changed."].Value);
        return RedirectToPage(new { Id });
    }
}

using MatMail.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Mailboxes;

public class AccessModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public string MailboxName { get; private set; } = string.Empty;
    public bool IsShared { get; private set; }
    public IReadOnlyList<PermissionRow> Permissions { get; private set; } = Array.Empty<PermissionRow>();

    public sealed record PermissionRow(long Id, string UserName, string LoginName, MailboxAccess Access);

    public async Task<IActionResult> OnGetAsync()
    {
        Mailbox? mailbox = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        MailboxName = mailbox.Name;
        IsShared = mailbox.Type == MailboxType.Shared;
        Permissions = await db.MailboxPermissions.AsNoTracking()
            .Where(p => p.MailboxId == Id)
            .OrderBy(p => p.User!.DisplayName)
            .Select(p => new PermissionRow(p.Id, p.User!.DisplayName, p.User.LoginName, p.Access))
            .ToListAsync();
        return Page();
    }

    public string AccessLabel(MailboxAccess access) => access switch
    {
        MailboxAccess.Read => l["Read"].Value,
        MailboxAccess.Edit => l["Read and edit"].Value,
        MailboxAccess.Send => l["Read, edit and send"].Value,
        _ => l["Full control"].Value,
    };
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Transfers;

public class DetailsModel(MatMailDbContext db, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public MailTransfer? Item { get; private set; }
    public TransferLabels Labels { get; } = new(l);

    /// <summary>The queue entry of an outgoing message, while it still exists.</summary>
    public bool QueueEntryExists { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        long? tenantId = currentUser.TenantId;
        bool system = currentUser.IsSystemAdmin;
        Item = await db.MailTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id && (t.TenantId == tenantId || (system && t.TenantId == null)));
        if (Item is null)
        {
            return NotFound();
        }

        if (Item.OutboundMessageId is long queueId && currentUser.Can(Permissions.QueueManage))
        {
            QueueEntryExists = await db.OutboundMessages.AnyAsync(o => o.Id == queueId);
        }

        return Page();
    }
}

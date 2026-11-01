using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Transfers;

/// <summary>One line of the mail transfer log of the tenant the person works in (the platform shows the same page for any line: <c>_TransferDetails</c>).</summary>
public class DetailsModel(LogListService logs, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public MailTransfer? Item { get; private set; }

    public TransferLabels Labels { get; } = new(l);

    /// <summary>The queue entry of an outgoing message, while it still exists.</summary>
    public bool QueueEntryExists { get; private set; }

    /// <summary>The list this page belongs to.</summary>
    public virtual string ListPath => "/Admin/Transfers";

    /// <summary>The queue of the tenant can be opened from here (not on the platform: the entry belongs to the administration of some tenant).</summary>
    public virtual bool ShowQueueLink => true;

    /// <summary>Which lines may be opened: those of the tenant the person works in.</summary>
    protected virtual LogScope Scope => LogScope.OfTenant(currentUser.TenantId);

    public async Task<IActionResult> OnGetAsync()
    {
        Item = await logs.TransferAsync(Id, Scope);
        if (Item is null)
        {
            return NotFound();
        }

        if (ShowQueueLink && Item.OutboundMessageId is long queueId && currentUser.Can(Permissions.QueueManage))
        {
            QueueEntryExists = await logs.QueueEntryExistsAsync(queueId);
        }

        return Page();
    }
}

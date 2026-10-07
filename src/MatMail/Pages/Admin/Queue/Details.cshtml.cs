using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Queue;

public class DetailsModel(MatMailDbContext db, OutboundQueue queue, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public OutboundMessage? Item { get; private set; }
    public string? AccountName { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Item = await db.OutboundMessages.AsNoTracking().Select(o => new OutboundMessage
        {
            Id = o.Id, TenantId = o.TenantId, EnvelopeFrom = o.EnvelopeFrom, Recipients = o.Recipients, Subject = o.Subject, SizeBytes = o.SizeBytes, Status = o.Status,
            AttemptCount = o.AttemptCount, NextAttemptDate = o.NextAttemptDate, SentDate = o.SentDate, LastError = o.LastError, CreateDate = o.CreateDate, MailAccountId = o.MailAccountId,
        }).FirstOrDefaultAsync(o => o.Id == Id);
        if (Item is null)
        {
            return NotFound();
        }

        AccountName = Item.MailAccountId is long accountId ? await db.MailAccounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.Name).FirstOrDefaultAsync() : null;
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        byte[]? raw = await db.OutboundMessages.AsNoTracking().Where(o => o.Id == Id).Select(o => o.Raw).FirstOrDefaultAsync();
        return raw is null ? NotFound() : File(raw, "message/rfc822", $"queued-{Id}.eml");
    }

    public async Task<IActionResult> OnPostRetryAsync(long id)
    {
        this.Notify(await queue.RetryNowAsync(id) ? l["The message is tried again now."].Value : l["The message cannot be tried again."].Value);
        return RedirectToPage(new { Id = id });
    }

    public async Task<IActionResult> OnPostCancelAsync(long id)
    {
        this.Notify(await queue.CancelAsync(id) ? l["Sending was cancelled."].Value : l["The message cannot be cancelled."].Value);
        return RedirectToPage(new { Id = id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await db.OutboundMessages.Where(o => o.Id == id && o.Status != OutboundStatus.Sending).ExecuteDeleteAsync();
        this.Notify(l["The entry was removed."].Value);
        return RedirectToPage("Index");
    }
}

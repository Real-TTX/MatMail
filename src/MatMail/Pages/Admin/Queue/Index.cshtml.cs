using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Queue;

public class IndexModel(MatMailDbContext db, OutboundQueue queue, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "created_desc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<QueueRow> Paged { get; private set; } = new(Array.Empty<QueueRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record QueueRow(
        long Id, string Subject, string EnvelopeFrom, string[] Recipients, long SizeBytes, OutboundStatus Status, int AttemptCount,
        DateTime NextAttemptDate, DateTime? SentDate, DateTime UpdateDate, string? LastError);

    public async Task OnGetAsync()
    {
        IQueryable<OutboundMessage> query = db.OutboundMessages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(o => EF.Functions.ILike(o.Subject, pattern) || EF.Functions.ILike(o.EnvelopeFrom, pattern)
                                     || (o.LastError != null && EF.Functions.ILike(o.LastError, pattern))
                                     || o.Recipients.Any(r => EF.Functions.ILike(r, pattern)));
        }

        if (Enum.TryParse(Status, out OutboundStatus status))
        {
            query = query.Where(o => o.Status == status);
        }

        query = Sort switch
        {
            "created_asc" => query.OrderBy(o => o.CreateDate),
            "next_asc" => query.OrderBy(o => o.NextAttemptDate),
            _ => query.OrderByDescending(o => o.CreateDate),
        };

        Paged = await query
            .Select(o => new QueueRow(o.Id, o.Subject, o.EnvelopeFrom, o.Recipients, o.SizeBytes, o.Status, o.AttemptCount, o.NextAttemptDate, o.SentDate, o.UpdateDate, o.LastError))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }

    public async Task<IActionResult> OnPostRetryAsync(long id)
    {
        this.Notify(await queue.RetryNowAsync(id) ? l["The message is tried again now."].Value : l["The message cannot be tried again."].Value);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAllFailedAsync()
    {
        long[] ids = await db.OutboundMessages.Where(o => o.Status == OutboundStatus.Failed).Select(o => o.Id).ToArrayAsync();
        foreach (long id in ids)
        {
            await queue.RetryNowAsync(id);
        }

        this.Notify(string.Format(l["{0} message(s) are tried again."].Value, ids.Length));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearFinishedAsync()
    {
        int removed = await db.OutboundMessages.Where(o => o.Status == OutboundStatus.Sent || o.Status == OutboundStatus.Cancelled).ExecuteDeleteAsync();
        this.Notify(string.Format(l["{0} entries were removed."].Value, removed));
        return RedirectToPage();
    }
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Unassigned;

public class IndexModel(MatMailDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "newest";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<MailMessage> Paged { get; private set; } = new(Array.Empty<MailMessage>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        IQueryable<MailMessage> query = db.MailMessages.AsNoTracking()
            .Where(m => m.Folder!.Kind == FolderKind.Inbox && m.Folder.Mailbox!.Type == MailboxType.Unassigned);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(m => EF.Functions.ILike(m.Subject, pattern) || EF.Functions.ILike(m.FromAddress, pattern) || EF.Functions.ILike(m.FromName, pattern)
                                     || (m.EnvelopeRecipients != null && EF.Functions.ILike(m.EnvelopeRecipients, pattern)));
        }

        query = Sort switch
        {
            "oldest" => query.OrderBy(m => m.ReceivedDate),
            "address" => query.OrderBy(m => m.EnvelopeRecipients).ThenByDescending(m => m.ReceivedDate),
            _ => query.OrderByDescending(m => m.ReceivedDate),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

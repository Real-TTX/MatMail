using System.Globalization;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Transfers;

/// <summary>The mail transfer log: what came into the server, what went out and what moved between mailboxes, and how it ended.</summary>
public class IndexModel(MatMailDbContext db, CurrentUser currentUser, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Direction { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Channel { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "newest";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public TransferLabels Labels { get; } = new(l);
    public PageResult<MailTransfer> Paged { get; private set; } = new(Array.Empty<MailTransfer>(), 0, 1, 1, PageSize);

    /// <summary>The log is switched off (<c>Retention.TransferLogDays</c> is 0): the page says so instead of showing an empty list.</summary>
    public bool LogIsOff => config.Retention.TransferLogDays <= 0;
    public int KeptDays => config.Retention.TransferLogDays;
    public bool ShowsSubjects => config.Retention.TransferLogSubjects;

    public async Task OnGetAsync()
    {
        long? tenantId = currentUser.TenantId;
        bool system = currentUser.IsSystemAdmin;

        // A tenant sees its own mail; what no tenant could be found for (refused mail for unknown domains) is for system administrators.
        IQueryable<MailTransfer> query = db.MailTransfers.AsNoTracking().Where(t => t.TenantId == tenantId || (system && t.TenantId == null));

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(t => EF.Functions.ILike(t.Sender, pattern) || EF.Functions.ILike(t.Recipients, pattern) || EF.Functions.ILike(t.Subject, pattern)
                                     || (t.MessageIdHeader != null && EF.Functions.ILike(t.MessageIdHeader, pattern))
                                     || (t.Peer != null && EF.Functions.ILike(t.Peer, pattern))
                                     || (t.RemoteIp != null && EF.Functions.ILike(t.RemoteIp, pattern))
                                     || (t.Detail != null && EF.Functions.ILike(t.Detail, pattern)));
        }

        if (Enum.TryParse(Direction, out TransferDirection direction))
        {
            query = query.Where(t => t.Direction == direction);
        }

        if (Enum.TryParse(Channel, out TransferChannel channel))
        {
            query = query.Where(t => t.Channel == channel);
        }

        if (Enum.TryParse(Status, out TransferStatus status))
        {
            query = query.Where(t => t.Status == status);
        }

        if (DateTime.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime from))
        {
            query = query.Where(t => t.CreateDate >= from);
        }

        if (DateTime.TryParseExact(To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime to))
        {
            DateTime end = to.AddDays(1);
            query = query.Where(t => t.CreateDate < end);
        }

        query = Sort switch
        {
            "oldest" => query.OrderBy(t => t.CreateDate).ThenBy(t => t.Id),
            "largest" => query.OrderByDescending(t => t.SizeBytes).ThenByDescending(t => t.Id),
            _ => query.OrderByDescending(t => t.CreateDate).ThenByDescending(t => t.Id),
        };

        Paged = await query.ToPageAsync(PageNumber, PageSize);
        PageNumber = Paged.PageNumber;
    }
}

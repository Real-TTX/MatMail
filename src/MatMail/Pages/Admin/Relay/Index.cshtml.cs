using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin.Relay;

public class IndexModel(MatMailDbContext db, AppConfig config) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<RelayRule> Paged { get; private set; } = new(Array.Empty<RelayRule>(), 0, 1, 1, Pager.DefaultPageSize);
    public string Hostname => config.Server.Hostname;
    public string SmtpPorts => string.Join(" / ", new[] { config.Smtp.Port, config.Smtp.SubmissionPort, config.Smtp.ImplicitTlsPort }.Where(p => p > 0));

    public async Task OnGetAsync()
    {
        IQueryable<RelayRule> query = db.RelayRules.AsNoTracking().Include(r => r.SendAccount);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern) || EF.Functions.ILike(r.Network, pattern));
        }

        query = Status switch
        {
            "enabled" => query.Where(r => r.IsEnabled),
            "disabled" => query.Where(r => !r.IsEnabled),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(r => r.Name),
            "created_desc" => query.OrderByDescending(r => r.CreateDate),
            _ => query.OrderBy(r => r.Name),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Templates;

public class IndexModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "priority";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<MailTemplate> Paged { get; private set; } = new(Array.Empty<MailTemplate>(), 0, 1, 1, Pager.DefaultPageSize);
    private Dictionary<long, string> _mailboxNames = new();
    private Dictionary<long, string> _userNames = new();
    private Dictionary<long, string> _ruleNames = new();

    public async Task OnGetAsync()
    {
        IQueryable<MailTemplate> query = db.MailTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Html, pattern));
        }

        if (Enum.TryParse(Scope, out AppliesTo scope))
        {
            query = query.Where(t => t.Scope == scope);
        }

        query = Sort switch
        {
            "name_asc" => query.OrderBy(t => t.Name),
            "name_desc" => query.OrderByDescending(t => t.Name),
            "created_desc" => query.OrderByDescending(t => t.CreateDate),
            _ => query.OrderBy(t => t.Priority).ThenBy(t => t.Name),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
        _mailboxNames = await db.Mailboxes.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name);
        _userNames = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        _ruleNames = await db.RelayRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
    }

    public string Target(MailTemplate template) => template.Scope switch
    {
        AppliesTo.Mailbox => l["Mailbox"].Value + ": " + (template.MailboxId is long m && _mailboxNames.TryGetValue(m, out string? mailbox) ? mailbox : "?"),
        AppliesTo.User => l["User"].Value + ": " + (template.UserId is long u && _userNames.TryGetValue(u, out string? user) ? user : "?"),
        _ => l["The whole tenant"].Value,
    };

    /// <summary>Where the messages must come from, as words: "Web client, Smart host (Office printers)".</summary>
    public string Sources(MailTemplate template)
    {
        var parts = new List<string>();
        if (template.ForWebClient)
        {
            parts.Add(l["Web client"].Value);
        }

        if (template.ForMailPrograms)
        {
            parts.Add(l["Mail programs"].Value);
        }

        if (template.ForSmartHost)
        {
            string rule = template.RelayRuleId is long id ? " (" + (_ruleNames.TryGetValue(id, out string? name) ? name : "?") + ")" : string.Empty;
            parts.Add(l["Smart host"].Value + rule);
        }

        return string.Join(", ", parts);
    }
}

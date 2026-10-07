using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Signatures;

public class IndexModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<Signature> Paged { get; private set; } = new(Array.Empty<Signature>(), 0, 1, 1, Pager.DefaultPageSize);
    private Dictionary<long, string> _mailboxNames = new();
    private Dictionary<long, string> _userNames = new();

    public async Task OnGetAsync()
    {
        IQueryable<Signature> query = db.Signatures.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(s => EF.Functions.ILike(s.Name, pattern) || EF.Functions.ILike(s.Html, pattern));
        }

        if (Enum.TryParse(Kind, out SignatureKind kind))
        {
            query = query.Where(s => s.Kind == kind);
        }

        if (Enum.TryParse(Scope, out AppliesTo scope))
        {
            query = query.Where(s => s.Scope == scope);
        }

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(s => s.Name),
            "created_desc" => query.OrderByDescending(s => s.CreateDate),
            _ => query.OrderBy(s => s.Name),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
        _mailboxNames = await db.Mailboxes.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name);
        _userNames = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
    }

    public string Target(Signature signature) => signature.Scope switch
    {
        AppliesTo.Mailbox => l["Mailbox"].Value + ": " + (signature.MailboxId is long m && _mailboxNames.TryGetValue(m, out string? mailbox) ? mailbox : "?"),
        AppliesTo.User => l["User"].Value + ": " + (signature.UserId is long u && _userNames.TryGetValue(u, out string? user) ? user : "?"),
        _ => l["The whole tenant"].Value,
    };
}

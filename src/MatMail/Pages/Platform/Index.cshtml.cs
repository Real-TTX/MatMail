using MatMail.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Platform;

/// <summary>The start page of the platform: every tenant with what is in it. Counted per kind, one query each (see <see cref="PlatformListModel"/> on why not joined).</summary>
public class IndexModel(MatMailDbContext db) : PageModel
{
    public sealed record TenantRow(long Id, string Name, bool IsActive, int Users, int Members, int Mailboxes, int Domains, int Accounts, int AccountErrors);

    public IReadOnlyList<TenantRow> Rows { get; private set; } = Array.Empty<TenantRow>();

    public async Task OnGetAsync()
    {
        var tenants = await db.Tenants.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive }).ToListAsync();
        Dictionary<long, int> users = await CountAsync(db.Users.IgnoreQueryFilters().Select(u => u.TenantId));
        Dictionary<long, int> members = await CountAsync(db.TenantMemberships.IgnoreQueryFilters().Select(m => m.TenantId));
        Dictionary<long, int> mailboxes = await CountAsync(db.Mailboxes.IgnoreQueryFilters().Where(m => m.Type != MailboxType.Unassigned).Select(m => m.TenantId));
        Dictionary<long, int> domains = await CountAsync(db.Domains.IgnoreQueryFilters().Select(d => d.TenantId));
        Dictionary<long, int> accounts = await CountAsync(db.MailAccounts.IgnoreQueryFilters().Select(a => a.TenantId));
        Dictionary<long, int> errors = await CountAsync(db.MailAccounts.IgnoreQueryFilters().Where(a => a.IsEnabled && a.LastSyncState == SyncState.Error).Select(a => a.TenantId));

        int Of(Dictionary<long, int> counts, long id) => counts.GetValueOrDefault(id);
        Rows = tenants.Select(t => new TenantRow(
            t.Id, t.Name, t.IsActive, Of(users, t.Id), Of(members, t.Id), Of(mailboxes, t.Id), Of(domains, t.Id), Of(accounts, t.Id), Of(errors, t.Id))).ToList();
    }

    private static async Task<Dictionary<long, int>> CountAsync(IQueryable<long> tenantIds)
        => await tenantIds.GroupBy(id => id).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count);
}

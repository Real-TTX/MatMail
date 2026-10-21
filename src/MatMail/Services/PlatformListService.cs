using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>What a list of the platform asks for: the search text, a tenant, a status filter, the sort and the page (the toolbar of the page).</summary>
public sealed record PlatformListQuery(string? Search, long? TenantId, string? Status, string? Sort, int PageNumber);

/// <summary>A tenant on the start page of the platform with what is in it.</summary>
public sealed record PlatformTenantRow(long Id, string Name, bool IsActive, int Users, int Members, int Mailboxes, int Domains, int Accounts, int AccountErrors);

/// <summary>A domain in the list across all tenants.</summary>
public sealed record PlatformDomainRow(long Id, long TenantId, string Name, string? Notes, bool IsActive, string? CatchAllMailbox, int Addresses);

/// <summary>A mailbox in the list across all tenants.</summary>
public sealed record PlatformMailboxRow(
    long Id, long TenantId, string Name, MailboxType Type, bool IsActive, string? OwnerName, string? PrimaryAddress, int AddressCount, int MessageCount, long UsedBytes, long? QuotaBytes);

/// <summary>A user in the list across all tenants.</summary>
public sealed record PlatformUserRow(
    long Id, long TenantId, string LoginName, string DisplayName, string? Email, bool IsActive, bool IsSystemAdmin, DateTime? LastLoginDate,
    DateTime? LockedUntilDate, bool BlockedByDirectory, bool InDirectory, bool TwoFactor, int Memberships);

/// <summary>
/// The lists of the platform (<c>Pages/Platform</c>, system administrators only): the tenants with their counts and one list each of the
/// connected accounts, mailboxes, domains and users of <b>all</b> tenants. Every query reads with <c>IgnoreQueryFilters()</c>, which switches the
/// tenant filter off for the whole query (subqueries included) – exactly what these lists are for. Never join such a set with a tenant-filtered
/// one: read the other side in a second query (the counts of <see cref="TenantsAsync"/> are one query per kind for that reason).
/// </summary>
public sealed class PlatformListService(MatMailDbContext db)
{
    /// <summary>The tenants by name (for the tenant filter and column of the lists).</summary>
    public async Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancel = default)
        => await db.Tenants.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancel);

    /// <summary>Every tenant with its users, members of other tenants, mailboxes, domains, connected accounts and accounts in error.</summary>
    public async Task<IReadOnlyList<PlatformTenantRow>> TenantsAsync(CancellationToken cancel = default)
    {
        var tenants = await db.Tenants.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive }).ToListAsync(cancel);
        Dictionary<long, int> users = await CountAsync(db.Users.IgnoreQueryFilters().Select(u => u.TenantId), cancel);
        Dictionary<long, int> members = await CountAsync(db.TenantMemberships.IgnoreQueryFilters().Select(m => m.TenantId), cancel);
        Dictionary<long, int> mailboxes = await CountAsync(db.Mailboxes.IgnoreQueryFilters().Where(m => m.Type != MailboxType.Unassigned).Select(m => m.TenantId), cancel);
        Dictionary<long, int> domains = await CountAsync(db.Domains.IgnoreQueryFilters().Select(d => d.TenantId), cancel);
        Dictionary<long, int> accounts = await CountAsync(db.MailAccounts.IgnoreQueryFilters().Select(a => a.TenantId), cancel);
        Dictionary<long, int> errors = await CountAsync(db.MailAccounts.IgnoreQueryFilters().Where(a => a.IsEnabled && a.LastSyncState == SyncState.Error).Select(a => a.TenantId), cancel);

        static int Of(Dictionary<long, int> counts, long id) => counts.GetValueOrDefault(id);
        return tenants.Select(t => new PlatformTenantRow(
            t.Id, t.Name, t.IsActive, Of(users, t.Id), Of(members, t.Id), Of(mailboxes, t.Id), Of(domains, t.Id), Of(accounts, t.Id), Of(errors, t.Id))).ToList();
    }

    /// <summary>The connected accounts of all tenants. Status: <c>enabled</c>, <c>disabled</c>, <c>error</c>; sort: <c>name_desc</c>, <c>sync_desc</c>, <c>created_desc</c>, else by name.</summary>
    public async Task<PageResult<MailAccount>> AccountsAsync(PlatformListQuery list, MailAccountRole? role)
    {
        IQueryable<MailAccount> query = db.MailAccounts.IgnoreQueryFilters().AsNoTracking();
        if (list.TenantId is long tenantId)
        {
            query = query.Where(a => a.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(list.Search))
        {
            string pattern = Pager.ContainsPattern(list.Search);
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Address, pattern)
                                     || (a.ReceiveHost != null && EF.Functions.ILike(a.ReceiveHost, pattern))
                                     || (a.SendHost != null && EF.Functions.ILike(a.SendHost, pattern)));
        }

        if (role is MailAccountRole wanted)
        {
            query = query.Where(a => a.Role == wanted);
        }

        query = list.Status switch
        {
            "enabled" => query.Where(a => a.IsEnabled),
            "disabled" => query.Where(a => !a.IsEnabled),
            "error" => query.Where(a => a.LastSyncState == SyncState.Error),
            _ => query,
        };

        query = list.Sort switch
        {
            "name_desc" => query.OrderByDescending(a => a.Name),
            "sync_desc" => query.OrderByDescending(a => a.LastSyncDate),
            "created_desc" => query.OrderByDescending(a => a.CreateDate),
            _ => query.OrderBy(a => a.Name),
        };

        return await query.ToPageAsync(list.PageNumber);
    }

    /// <summary>The domains of all tenants. Status: <c>active</c>, <c>inactive</c>; sort: <c>name_desc</c>, <c>addresses_desc</c>, else by name.</summary>
    public async Task<PageResult<PlatformDomainRow>> DomainsAsync(PlatformListQuery list)
    {
        IQueryable<Domain> query = db.Domains.IgnoreQueryFilters().AsNoTracking();
        if (list.TenantId is long tenantId)
        {
            query = query.Where(d => d.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(list.Search))
        {
            string pattern = Pager.ContainsPattern(list.Search);
            query = query.Where(d => EF.Functions.ILike(d.Name, pattern) || (d.Notes != null && EF.Functions.ILike(d.Notes, pattern)));
        }

        query = list.Status switch
        {
            "active" => query.Where(d => d.IsActive),
            "inactive" => query.Where(d => !d.IsActive),
            _ => query,
        };

        query = list.Sort switch
        {
            "name_desc" => query.OrderByDescending(d => d.Name),
            "addresses_desc" => query.OrderByDescending(d => db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))).ThenBy(d => d.Name),
            _ => query.OrderBy(d => d.Name),
        };

        return await query
            .Select(d => new PlatformDomainRow(
                d.Id, d.TenantId, d.Name, d.Notes, d.IsActive,
                d.CatchAllMailbox != null ? d.CatchAllMailbox.Name : null,
                db.MailboxAliases.Count(a => a.Address.EndsWith("@" + d.Name))))
            .ToPageAsync(list.PageNumber);
    }

    /// <summary>
    /// The mailboxes of all tenants (not the <i>Unassigned</i> buckets). Status: <c>active</c>, <c>inactive</c>, <c>full</c>; sort: <c>name_desc</c>,
    /// <c>size_desc</c>, <c>created_desc</c>, else by name.
    /// </summary>
    public async Task<PageResult<PlatformMailboxRow>> MailboxesAsync(PlatformListQuery list, MailboxType? type)
    {
        IQueryable<Mailbox> query = db.Mailboxes.IgnoreQueryFilters().AsNoTracking().Where(m => m.Type != MailboxType.Unassigned);
        if (list.TenantId is long tenantId)
        {
            query = query.Where(m => m.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(list.Search))
        {
            string pattern = Pager.ContainsPattern(list.Search);
            query = query.Where(m => EF.Functions.ILike(m.Name, pattern)
                                     || db.MailboxAliases.Any(a => a.MailboxId == m.Id && EF.Functions.ILike(a.Address, pattern))
                                     || (m.OwnerUser != null && EF.Functions.ILike(m.OwnerUser.DisplayName, pattern)));
        }

        if (type is MailboxType wanted && wanted != MailboxType.Unassigned)
        {
            query = query.Where(m => m.Type == wanted);
        }

        query = list.Status switch
        {
            "active" => query.Where(m => m.IsActive),
            "inactive" => query.Where(m => !m.IsActive),
            "full" => query.Where(m => m.QuotaBytes != null && db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes) >= m.QuotaBytes),
            _ => query,
        };

        query = list.Sort switch
        {
            "name_desc" => query.OrderByDescending(m => m.Name),
            "size_desc" => query.OrderByDescending(m => db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes)).ThenBy(m => m.Name),
            "created_desc" => query.OrderByDescending(m => m.CreateDate),
            _ => query.OrderBy(m => m.Name),
        };

        return await query
            .Select(m => new PlatformMailboxRow(
                m.Id,
                m.TenantId,
                m.Name,
                m.Type,
                m.IsActive,
                m.OwnerUser != null ? m.OwnerUser.DisplayName : null,
                db.MailboxAliases.Where(a => a.MailboxId == m.Id).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Address).Select(a => a.Address).FirstOrDefault(),
                db.MailboxAliases.Count(a => a.MailboxId == m.Id),
                db.MailMessages.Count(x => x.MailboxId == m.Id),
                db.MailMessages.Where(x => x.MailboxId == m.Id && x.Storage == MessageStorage.Local).Sum(x => x.SizeBytes),
                m.QuotaBytes))
            .ToPageAsync(list.PageNumber);
    }

    /// <summary>
    /// The users of all tenants. Status: <c>active</c>, <c>inactive</c>, <c>locked</c>, <c>admins</c> (system administrators), <c>members</c> (of
    /// other tenants); sort: <c>name_desc</c>, <c>login_desc</c>, <c>created_desc</c>, else by name.
    /// </summary>
    public async Task<PageResult<PlatformUserRow>> UsersAsync(PlatformListQuery list)
    {
        DateTime now = DateTime.UtcNow;
        IQueryable<User> query = db.Users.IgnoreQueryFilters().AsNoTracking();
        if (list.TenantId is long tenantId)
        {
            query = query.Where(u => u.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(list.Search))
        {
            string pattern = Pager.ContainsPattern(list.Search);
            query = query.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.LoginName, pattern)
                                     || (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        query = list.Status switch
        {
            "active" => query.Where(u => u.IsActive),
            "inactive" => query.Where(u => !u.IsActive),
            "locked" => query.Where(u => u.LockedUntilDate > now),
            "admins" => query.Where(u => u.IsSystemAdmin),
            "members" => query.Where(u => db.TenantMemberships.Any(m => m.UserId == u.Id)),
            _ => query,
        };

        query = list.Sort switch
        {
            "name_desc" => query.OrderByDescending(u => u.DisplayName),
            "login_desc" => query.OrderByDescending(u => u.LastLoginDate),
            "created_desc" => query.OrderByDescending(u => u.CreateDate),
            _ => query.OrderBy(u => u.DisplayName),
        };

        return await query
            .Select(u => new PlatformUserRow(
                u.Id, u.TenantId, u.LoginName, u.DisplayName, u.Email, u.IsActive, u.IsSystemAdmin, u.LastLoginDate, u.LockedUntilDate,
                u.DirectoryDisabledDate != null, u.DirectoryId != null,
                db.UserTotps.Any(t => t.UserId == u.Id && t.ConfirmedDate != null),
                db.TenantMemberships.Count(m => m.UserId == u.Id)))
            .ToPageAsync(list.PageNumber);
    }

    private static async Task<Dictionary<long, int>> CountAsync(IQueryable<long> tenantIds, CancellationToken cancel)
        => await tenantIds.GroupBy(id => id).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, cancel);
}

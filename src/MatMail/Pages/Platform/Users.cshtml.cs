using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Platform;

public class UsersModel(MatMailDbContext db) : PlatformListModel(db)
{
    public PageResult<UserRow> Paged { get; private set; } = new(Array.Empty<UserRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public sealed record UserRow(
        long Id, long TenantId, string LoginName, string DisplayName, string? Email, bool IsActive, bool IsSystemAdmin, DateTime? LastLoginDate,
        DateTime? LockedUntilDate, bool BlockedByDirectory, bool InDirectory, bool TwoFactor, int Memberships);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        DateTime now = DateTime.UtcNow;
        IQueryable<User> query = Db.Users.IgnoreQueryFilters().AsNoTracking();
        if (TenantId is long tenantId)
        {
            query = query.Where(u => u.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.LoginName, pattern)
                                     || (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        query = Status switch
        {
            "active" => query.Where(u => u.IsActive),
            "inactive" => query.Where(u => !u.IsActive),
            "locked" => query.Where(u => u.LockedUntilDate > now),
            "admins" => query.Where(u => u.IsSystemAdmin),
            "members" => query.Where(u => Db.TenantMemberships.Any(m => m.UserId == u.Id)),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(u => u.DisplayName),
            "login_desc" => query.OrderByDescending(u => u.LastLoginDate),
            "created_desc" => query.OrderByDescending(u => u.CreateDate),
            _ => query.OrderBy(u => u.DisplayName),
        };

        Paged = await query
            .Select(u => new UserRow(
                u.Id, u.TenantId, u.LoginName, u.DisplayName, u.Email, u.IsActive, u.IsSystemAdmin, u.LastLoginDate, u.LockedUntilDate,
                u.DirectoryDisabledDate != null, u.DirectoryId != null,
                Db.UserTotps.Any(t => t.UserId == u.Id && t.ConfirmedDate != null),
                Db.TenantMemberships.Count(m => m.UserId == u.Id)))
            .ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }
}

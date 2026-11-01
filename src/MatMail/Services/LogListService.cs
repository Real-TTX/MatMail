using System.Globalization;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// Which rows a log list shows. In the administration of a tenant: the rows of that tenant and nothing else (what belongs to the
/// installation is the platform's). On the platform: every tenant (<see cref="Tenant"/> null), the installation itself (0: the rows without
/// a tenant) or one tenant.
/// </summary>
public sealed record LogScope(bool Platform, long? Tenant)
{
    public static LogScope OfTenant(long? tenantId) => new(false, tenantId);

    public static LogScope OfPlatform(long? filter) => new(true, filter);
}

/// <summary>The toolbar of the activity log.</summary>
public sealed record ActivityFilter(string? Search, string? Category, string? Level, string? From, string? To, string? Sort, int PageNumber);

/// <summary>The toolbar of the mail transfer log.</summary>
public sealed record TransferFilter(string? Search, string? Direction, string? Channel, string? Status, string? From, string? To, string? Sort, int PageNumber);

/// <summary>
/// The activity log and the mail transfer log as lists, for the administration of a tenant and for the platform alike (the pages hold no
/// database). Both tables are not filtered by tenant by the context (a row may belong to no tenant), so the <see cref="LogScope"/> is the
/// whole rule: a tenant never sees a row of another tenant or of the installation.
/// </summary>
public sealed class LogListService(MatMailDbContext db)
{
    public const int PageSize = 50;

    /// <summary>The tenants by name, for the tenant column and filter of the platform.</summary>
    public async Task<IReadOnlyList<Tenant>> TenantsAsync() => await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync();

    public async Task<PageResult<ActivityLog>> ActivityAsync(ActivityFilter filter, LogScope scope)
    {
        IQueryable<ActivityLog> query = scope switch
        {
            { Platform: true, Tenant: null } => db.ActivityLogs.AsNoTracking(),
            { Platform: true, Tenant: 0 } => db.ActivityLogs.AsNoTracking().Where(a => a.TenantId == null),
            { Tenant: long id } => db.ActivityLogs.AsNoTracking().Where(a => a.TenantId == id),
            _ => db.ActivityLogs.AsNoTracking().Where(_ => false),   // no tenant and not the platform: nothing (never the rows of the installation)
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string pattern = Pager.ContainsPattern(filter.Search);
            query = query.Where(a => EF.Functions.ILike(a.Message, pattern) || (a.Details != null && EF.Functions.ILike(a.Details, pattern))
                                     || (a.RemoteIp != null && EF.Functions.ILike(a.RemoteIp, pattern)));
        }

        if (Enum.TryParse(filter.Category, out ActivityCategory category))
        {
            query = query.Where(a => a.Category == category);
        }

        if (Enum.TryParse(filter.Level, out ActivityLevel level))
        {
            query = query.Where(a => a.Level == level);
        }

        if (Day(filter.From) is DateTime from)
        {
            query = query.Where(a => a.CreateDate >= from);
        }

        if (Day(filter.To) is DateTime to)
        {
            DateTime end = to.AddDays(1);
            query = query.Where(a => a.CreateDate < end);
        }

        query = filter.Sort == "oldest" ? query.OrderBy(a => a.CreateDate) : query.OrderByDescending(a => a.CreateDate);
        return await query.ToPageAsync(filter.PageNumber, PageSize);
    }

    public async Task<PageResult<MailTransfer>> TransfersAsync(TransferFilter filter, LogScope scope)
    {
        IQueryable<MailTransfer> query = Transfers(scope);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string pattern = Pager.ContainsPattern(filter.Search);
            query = query.Where(t => EF.Functions.ILike(t.Sender, pattern) || EF.Functions.ILike(t.Recipients, pattern) || EF.Functions.ILike(t.Subject, pattern)
                                     || (t.MessageIdHeader != null && EF.Functions.ILike(t.MessageIdHeader, pattern))
                                     || (t.Peer != null && EF.Functions.ILike(t.Peer, pattern))
                                     || (t.RemoteIp != null && EF.Functions.ILike(t.RemoteIp, pattern))
                                     || (t.Detail != null && EF.Functions.ILike(t.Detail, pattern)));
        }

        if (Enum.TryParse(filter.Direction, out TransferDirection direction))
        {
            query = query.Where(t => t.Direction == direction);
        }

        if (Enum.TryParse(filter.Channel, out TransferChannel channel))
        {
            query = query.Where(t => t.Channel == channel);
        }

        if (Enum.TryParse(filter.Status, out TransferStatus status))
        {
            query = query.Where(t => t.Status == status);
        }

        if (Day(filter.From) is DateTime from)
        {
            query = query.Where(t => t.CreateDate >= from);
        }

        if (Day(filter.To) is DateTime to)
        {
            DateTime end = to.AddDays(1);
            query = query.Where(t => t.CreateDate < end);
        }

        query = filter.Sort switch
        {
            "oldest" => query.OrderBy(t => t.CreateDate).ThenBy(t => t.Id),
            "largest" => query.OrderByDescending(t => t.SizeBytes).ThenByDescending(t => t.Id),
            _ => query.OrderByDescending(t => t.CreateDate).ThenByDescending(t => t.Id),
        };

        return await query.ToPageAsync(filter.PageNumber, PageSize);
    }

    /// <summary>One line of the mail transfer log, if the scope may see it.</summary>
    public Task<MailTransfer?> TransferAsync(long id, LogScope scope) => Transfers(scope).FirstOrDefaultAsync(t => t.Id == id);

    /// <summary>Whether the queue entry of an outgoing message still exists (in the tenant the person works in).</summary>
    public Task<bool> QueueEntryExistsAsync(long queueId) => db.OutboundMessages.AnyAsync(o => o.Id == queueId);

    private IQueryable<MailTransfer> Transfers(LogScope scope) => scope switch
    {
        { Platform: true, Tenant: null } => db.MailTransfers.AsNoTracking(),
        { Platform: true, Tenant: 0 } => db.MailTransfers.AsNoTracking().Where(t => t.TenantId == null),
        { Tenant: long id } => db.MailTransfers.AsNoTracking().Where(t => t.TenantId == id),
        _ => db.MailTransfers.AsNoTracking().Where(_ => false),
    };

    private static DateTime? Day(string? text)
        => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day)
            ? day
            : null;
}

namespace MatMail.Data;

/// <summary>
/// Base class of every table: <c>Id</c> is always a BIGINT identity, and every row carries
/// the create/update audit columns (filled automatically in <see cref="MatMailDbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>).
/// A <c>null</c> user id means "the system" (background work such as sync or the SMTP server).
/// </summary>
public abstract class AuditedEntity
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
}

/// <summary>
/// Rows that belong to exactly one tenant. The DbContext adds a global query filter for the
/// tenant of the current user, so a request can never read another tenant's data by accident.
/// </summary>
public interface ITenantEntity
{
    long TenantId { get; set; }
}

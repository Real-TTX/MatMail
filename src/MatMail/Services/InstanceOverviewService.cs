using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>The numbers of the whole installation (all tenants) at one moment: what the platform contract shows as tiles.</summary>
/// <param name="StorageBytes">The messages kept on this server (live-access stand-ins do not count, like in the storage of a mailbox).</param>
/// <param name="QueueWaiting">Outgoing messages that are still to go out (waiting or being sent).</param>
/// <param name="QueueFailed">Outgoing messages the server gave up on.</param>
/// <param name="AccountErrors">Switched-on provider accounts whose last synchronisation failed.</param>
/// <param name="LastReceivedUtc">The newest message that came in from outside (the transfer log; null while it is empty or switched off).</param>
/// <param name="LastSentUtc">The newest message that went out (the outgoing queue keeps sent messages for some days).</param>
public sealed record InstanceSnapshot(
    int Tenants,
    int Users,
    int Mailboxes,
    int Domains,
    int Accounts,
    int AccountErrors,
    long Messages,
    long StorageBytes,
    int QueueWaiting,
    int QueueFailed,
    long Unassigned,
    DateTime? LastReceivedUtc,
    DateTime? LastSentUtc,
    DateTime? LastBackupUtc);

/// <summary>What could be wrong with the installation as a whole (for the health of the platform contract).</summary>
/// <param name="Database">PostgreSQL answers.</param>
/// <param name="HasUsers">The set-up page has created the first administrator.</param>
/// <param name="AccountErrors">Switched-on provider accounts whose last synchronisation failed.</param>
/// <param name="BackupFailed">An active backup plan whose last run failed.</param>
public sealed record InstanceHealth(bool Database, bool HasUsers, int AccountErrors, bool BackupFailed);

/// <summary>
/// The installation seen from outside (Mattiverse and the platform contract): counts across all tenants, its health, and the provider
/// accounts to fetch. Reads past the tenant filter on purpose: the platform looks at the installation, not at one tenant.
/// </summary>
public sealed class InstanceOverviewService(MatMailDbContext db)
{
    public async Task<InstanceSnapshot> GetSnapshotAsync(CancellationToken cancel = default)
    {
        int tenants = await db.Tenants.CountAsync(cancel);
        int users = await db.Users.IgnoreQueryFilters().CountAsync(cancel);
        int mailboxes = await db.Mailboxes.IgnoreQueryFilters().CountAsync(m => m.Type != MailboxType.Unassigned, cancel);
        int domains = await db.Domains.IgnoreQueryFilters().CountAsync(cancel);
        int accounts = await db.MailAccounts.IgnoreQueryFilters().CountAsync(cancel);
        int accountErrors = await CountAccountErrorsAsync(cancel);

        long messages = await db.MailMessages.IgnoreQueryFilters().LongCountAsync(cancel);
        long storageBytes = await db.MailMessages.IgnoreQueryFilters()
            .Where(m => m.Storage == MessageStorage.Local)
            .SumAsync(m => (long?)m.SizeBytes, cancel) ?? 0;

        int queueWaiting = await db.OutboundMessages.IgnoreQueryFilters()
            .CountAsync(o => o.Status == OutboundStatus.Pending || o.Status == OutboundStatus.Sending, cancel);
        int queueFailed = await db.OutboundMessages.IgnoreQueryFilters().CountAsync(o => o.Status == OutboundStatus.Failed, cancel);
        long unassigned = await db.MailMessages.IgnoreQueryFilters()
            .LongCountAsync(m => db.Mailboxes.IgnoreQueryFilters().Any(b => b.Id == m.MailboxId && b.Type == MailboxType.Unassigned), cancel);

        DateTime? lastReceived = await db.MailTransfers.IgnoreQueryFilters()
            .Where(t => t.Direction == TransferDirection.Inbound && t.Status == TransferStatus.Delivered)
            .MaxAsync(t => (DateTime?)t.CreateDate, cancel);
        DateTime? lastSent = await db.OutboundMessages.IgnoreQueryFilters()
            .Where(o => o.Status == OutboundStatus.Sent)
            .MaxAsync(o => o.SentDate, cancel);
        DateTime? lastBackup = await db.BackupRuns
            .Where(r => r.Status == BackupRunStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.StartedDate, cancel);

        return new InstanceSnapshot(
            tenants,
            users,
            mailboxes,
            domains,
            accounts,
            accountErrors,
            messages,
            storageBytes,
            queueWaiting,
            queueFailed,
            unassigned,
            lastReceived,
            lastSent,
            lastBackup);
    }

    /// <summary>A few cheap questions; a database that does not answer is reported, never thrown.</summary>
    public async Task<InstanceHealth> CheckHealthAsync(CancellationToken cancel = default)
    {
        if (!await db.Database.CanConnectAsync(cancel))
        {
            return new InstanceHealth(false, false, 0, false);
        }

        bool hasUsers = await db.Users.IgnoreQueryFilters().AnyAsync(cancel);
        int accountErrors = await CountAccountErrorsAsync(cancel);
        bool backupFailed = await db.BackupPlans.AnyAsync(p => p.IsActive && p.LastStatus == BackupRunStatus.Failed, cancel);
        return new InstanceHealth(true, hasUsers, accountErrors, backupFailed);
    }

    /// <summary>The provider accounts the background synchronisation fetches (switched on, with a way to receive), in all tenants.</summary>
    public async Task<IReadOnlyList<long>> ListFetchedAccountIdsAsync(CancellationToken cancel = default)
        => await db.MailAccounts.IgnoreQueryFilters()
            .Where(a => a.IsEnabled && a.Role != MailAccountRole.SendOnly && a.ReceiveProtocol != ReceiveProtocol.None)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .ToListAsync(cancel);

    private Task<int> CountAccountErrorsAsync(CancellationToken cancel)
        => db.MailAccounts.IgnoreQueryFilters().CountAsync(a => a.IsEnabled && a.LastSyncState == SyncState.Error, cancel);
}

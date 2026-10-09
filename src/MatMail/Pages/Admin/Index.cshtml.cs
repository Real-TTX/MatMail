using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Admin;

/// <summary>The administration dashboard: counts, problems and the latest events.</summary>
[Authorize]
public class IndexModel(MatMailDbContext db, CurrentUser currentUser, AppConfig config, CertificateProvider certificates) : PageModel
{
    public int UserCount { get; private set; }
    public int MailboxCount { get; private set; }
    public int DomainCount { get; private set; }
    public int AccountCount { get; private set; }
    public int QueuedCount { get; private set; }
    public long UnassignedCount { get; private set; }
    public string TenantName => currentUser.TenantName ?? string.Empty;
    public string Hostname => config.Server.Hostname;
    public bool IsSystemAdmin => currentUser.IsSystemAdmin;
    public CertificateInfo? Certificate => certificates.Describe();
    public IReadOnlyList<MailAccount> AccountProblems { get; private set; } = Array.Empty<MailAccount>();

    /// <summary>Directories that could not be asked at the last comparison: their people cannot sign in until it works again.</summary>
    public IReadOnlyList<DirectoryConnection> DirectoryProblems { get; private set; } = Array.Empty<DirectoryConnection>();
    public IReadOnlyList<ActivityLog> RecentLogs { get; private set; } = Array.Empty<ActivityLog>();

    /// <summary>The newest backup that worked (system administrators only), and whether any schedule exists or the last run of one failed.</summary>
    public DateTime? LastBackup { get; private set; }
    public int BackupPlans { get; private set; }
    public bool BackupFailed { get; private set; }

    public bool Can(string permission) => currentUser.Can(permission);

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnGetAsync()
    {
        if (!currentUser.CanAdminister)
        {
            return RedirectToPage("/Account/Index");
        }

        long? tenantId = currentUser.TenantId;
        if (IsSystemAdmin)
        {
            LastBackup = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupRunStatus.Succeeded).MaxAsync(r => (DateTime?)r.StartedDate);
            BackupPlans = await db.BackupPlans.CountAsync(p => p.IsActive);
            BackupFailed = await db.BackupPlans.AnyAsync(p => p.IsActive && p.LastStatus == BackupRunStatus.Failed);
        }

        if (Can(Permissions.UsersManage))
        {
            UserCount = await db.Users.CountAsync();
        }

        if (Can(Permissions.MailboxesManage))
        {
            MailboxCount = await db.Mailboxes.CountAsync(m => m.Type != MailboxType.Unassigned);
        }

        if (Can(Permissions.DomainsManage))
        {
            DomainCount = await db.Domains.CountAsync();
        }

        if (Can(Permissions.AccountsManage))
        {
            AccountCount = await db.MailAccounts.CountAsync();
            AccountProblems = await db.MailAccounts.AsNoTracking()
                .Where(a => a.IsEnabled && a.LastSyncState == SyncState.Error)
                .OrderBy(a => a.Name).Take(5).ToListAsync();
        }

        if (Can(Permissions.DirectoriesManage))
        {
            DirectoryProblems = await db.DirectoryConnections.AsNoTracking()
                .Where(d => d.IsActive && (d.LastSyncOk == false || (d.LastSyncDate == null && d.LastCheckOk == false)))   // once compared, the comparison tells
                .OrderBy(d => d.Name).Take(5).ToListAsync();
        }

        if (Can(Permissions.QueueManage))
        {
            QueuedCount = await db.OutboundMessages.CountAsync(o => o.Status == OutboundStatus.Pending || o.Status == OutboundStatus.Sending);
        }

        if (Can(Permissions.UnassignedManage))
        {
            UnassignedCount = await db.MailMessages.CountAsync(m => db.Mailboxes.Any(b => b.Id == m.MailboxId && b.Type == MailboxType.Unassigned));
        }

        if (Can(Permissions.LogsView))
        {
            RecentLogs = await db.ActivityLogs.AsNoTracking()
                .Where(l => l.TenantId == tenantId || l.TenantId == null)
                .OrderByDescending(l => l.CreateDate).Take(8).ToListAsync();
        }

        return Page();
    }
}

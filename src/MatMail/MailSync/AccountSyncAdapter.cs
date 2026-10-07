using MatMail.Messaging;

namespace MatMail.MailSync;

/// <summary>Gives the admin pages (<see cref="IAccountSyncRunner"/>) access to the synchronisation engine.</summary>
internal sealed class AccountSyncAdapter(MailSyncTrigger trigger) : IAccountSyncRunner
{
    public async Task<SyncOutcome> SyncNowAsync(long accountId, CancellationToken cancel = default)
    {
        SyncReport report = await trigger.SyncNowAsync(accountId, cancel);
        return new SyncOutcome(report.Started && report.Succeeded, report.Message);
    }

    public void Request(long accountId) => trigger.RequestSync(accountId);

    public Task<bool> ResetAsync(long accountId, CancellationToken cancel = default) => trigger.ResetAsync(accountId, cancel);
}

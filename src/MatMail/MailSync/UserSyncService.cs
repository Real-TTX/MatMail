using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailSync;

/// <summary>What "Synchronise now" of a user did, summed up over the accounts that feed their mailboxes.</summary>
/// <param name="Accounts">The accounts that are relevant for the user.</param>
/// <param name="Synced">Runs that ended without an error.</param>
/// <param name="Downloaded">New messages stored by those runs.</param>
/// <param name="Failed">Runs that failed or could not start.</param>
/// <param name="AlreadyRunning">Accounts that were being synchronised already (they bring the mail in by themselves).</param>
/// <param name="StillRunning">Runs that took longer than the user waited: they go on in the background.</param>
/// <param name="Problems">What went wrong, one line per account (for those who may administer accounts).</param>
public sealed record UserSyncResult(int Accounts, int Synced, int Downloaded, int Failed, int AlreadyRunning, int StillRunning, IReadOnlyList<string> Problems)
{
    public static readonly UserSyncResult None = new(0, 0, 0, 0, 0, 0, Array.Empty<string>());
}

/// <summary>
/// "Synchronise now" for everybody, not just for administrators: finds the connected accounts that feed the mailboxes of a user and runs
/// them right away. An account is relevant when mail for the user's mailboxes comes through it: it delivers into one of them, its own
/// address is an address of one of them, it collects a domain the mailboxes have addresses in, or it has delivered into one of them
/// before. Accounts that only send, are switched off, or belong to another tenant are left out.
/// </summary>
public sealed class UserSyncService
{
    /// <summary>How many accounts are fetched at the same time for one request.</summary>
    public const int MaxParallel = 4;

    private readonly MatMailDbContext _db;
    private readonly MailAccessService _access;
    private readonly MailSyncTrigger _trigger;
    private readonly IHostApplicationLifetime? _lifetime;

    public UserSyncService(MatMailDbContext db, MailAccessService access, MailSyncTrigger trigger, IServiceProvider services)
    {
        _db = db;
        _access = access;
        _trigger = trigger;
        _lifetime = services.GetService<IHostApplicationLifetime>();
    }

    public bool IsEnabled => _trigger.IsEnabled;

    /// <summary>The accounts whose mail ends up in the mailboxes the user can open.</summary>
    public async Task<IReadOnlyList<MailAccount>> FindRelevantAccountsAsync(MailUser user, CancellationToken cancel = default)
    {
        IReadOnlyList<AccessibleMailbox> boxes = await _access.GetMailboxesAsync(user, cancel);
        long[] mailboxIds = boxes.Select(b => b.Mailbox.Id).ToArray();
        if (mailboxIds.Length == 0)
        {
            return Array.Empty<MailAccount>();
        }

        List<MailAccount> candidates = await _db.MailAccounts.AsNoTracking()
            .Where(a => a.TenantId == user.TenantId && a.IsEnabled && a.Role != MailAccountRole.SendOnly && a.ReceiveProtocol != ReceiveProtocol.None)
            .OrderBy(a => a.Name)
            .ToListAsync(cancel);
        if (candidates.Count == 0)
        {
            return candidates;
        }

        string[] addresses = await _db.MailboxAliases.AsNoTracking().Where(a => mailboxIds.Contains(a.MailboxId)).Select(a => a.Address).ToArrayAsync(cancel);
        var own = new HashSet<string>(addresses.Where(a => !MailAddresses.IsCatchAll(a)), StringComparer.OrdinalIgnoreCase);
        var domains = new HashSet<string>(addresses.Select(MailAddresses.DomainOf).Where(d => d.Length > 0), StringComparer.OrdinalIgnoreCase);

        var relevant = new List<MailAccount>();
        foreach (MailAccount account in candidates)
        {
            if (await IsRelevantAsync(account, mailboxIds, own, domains, cancel))
            {
                relevant.Add(account);
            }
        }

        return relevant;
    }

    private async Task<bool> IsRelevantAsync(MailAccount account, long[] mailboxIds, HashSet<string> ownAddresses, HashSet<string> domains, CancellationToken cancel)
    {
        if (account.TargetMailboxId is long target && mailboxIds.Contains(target))
        {
            return true;
        }

        if (account.Role != MailAccountRole.Mail)
        {
            return false;
        }

        string address = MailAddresses.Normalize(account.Address);
        if (account.IsCatchAll ? domains.Contains(MailAddresses.DomainOf(address)) : ownAddresses.Contains(address))
        {
            return true;
        }

        return await _db.MailMessages.AnyAsync(m => m.SourceAccountId == account.Id && mailboxIds.Contains(m.MailboxId), cancel);
    }

    /// <summary>
    /// Synchronises the relevant accounts (up to <see cref="MaxParallel"/> at a time) and waits up to <paramref name="patience"/> for the
    /// result. Runs that take longer are not stopped: they finish in the background and the mail shows up when they do.
    /// </summary>
    public async Task<UserSyncResult> SyncAsync(MailUser user, TimeSpan patience, bool showProblems, CancellationToken cancel = default)
    {
        IReadOnlyList<MailAccount> accounts = await FindRelevantAccountsAsync(user, cancel);
        if (accounts.Count == 0)
        {
            return UserSyncResult.None;
        }

        // The runs belong to the server, not to this request: a browser that gives up must not interrupt a fetch half way.
        var gate = new SemaphoreSlim(MaxParallel);
        CancellationToken stopping = _lifetime?.ApplicationStopping ?? CancellationToken.None;
        List<(MailAccount Account, Task<SyncReport> Run)> runs = accounts.Select(a => (a, RunAsync(a.Id, gate, stopping))).ToList();

        try
        {
            await Task.WhenAny(Task.WhenAll(runs.Select(r => r.Run)), Task.Delay(patience, cancel));
        }
        catch (OperationCanceledException)
        {
            // The browser went away; whatever has finished is counted.
        }

        int synced = 0, downloaded = 0, failed = 0, already = 0, still = 0;
        var problems = new List<string>();
        foreach ((MailAccount account, Task<SyncReport> run) in runs)
        {
            if (!run.IsCompletedSuccessfully)
            {
                still++;
                continue;
            }

            SyncReport report = run.Result;
            if (report.AlreadyRunning)
            {
                already++;
            }
            else if (report.Started && report.Succeeded)
            {
                synced++;
                downloaded += report.Downloaded;
            }
            else
            {
                failed++;
                if (showProblems)
                {
                    problems.Add($"{account.Name}: {report.Message}");
                }
            }
        }

        return new UserSyncResult(accounts.Count, synced, downloaded, failed, already, still, problems);
    }

    private async Task<SyncReport> RunAsync(long accountId, SemaphoreSlim gate, CancellationToken stopping)
    {
        await gate.WaitAsync(stopping);
        try
        {
            return await _trigger.SyncNowAsync(accountId, stopping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncReport.NotStarted(accountId, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }
}

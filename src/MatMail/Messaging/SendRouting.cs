using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>Decides which connected provider account delivers an outgoing message (or whether it is delivered directly).</summary>
public sealed class SendRouting
{
    private readonly MatMailDbContext _db;

    public SendRouting(MatMailDbContext db) => _db = db;

    /// <summary>
    /// Order: the account chosen for the sender address, the account of the relay rule, an account with exactly the sender address,
    /// an account of the sender's domain. Null = no account: direct delivery (when allowed).
    /// </summary>
    public async Task<MailAccount?> ResolveAccountAsync(long tenantId, string fromAddress, RelayRule? rule = null, CancellationToken cancel = default)
    {
        string from = MailAddresses.Normalize(fromAddress);
        IQueryable<MailAccount> usable = _db.MailAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsEnabled && a.Role != MailAccountRole.Backup && a.SendHost != null && a.SendHost != "");

        long? aliasAccountId = from.Length == 0
            ? null
            : await _db.MailboxAliases.IgnoreQueryFilters().AsNoTracking().Where(a => a.Address == from).Select(a => a.SendAccountId).FirstOrDefaultAsync(cancel);
        if (aliasAccountId is long chosen)
        {
            MailAccount? account = await usable.FirstOrDefaultAsync(a => a.Id == chosen, cancel);
            if (account is not null)
            {
                return account;
            }
        }

        if (rule?.SendAccountId is long ruleAccountId)
        {
            MailAccount? account = await usable.FirstOrDefaultAsync(a => a.Id == ruleAccountId, cancel);
            if (account is not null)
            {
                return account;
            }
        }

        if (from.Length > 0)
        {
            MailAccount? exact = await usable.Where(a => a.Address == from).OrderBy(a => a.Id).FirstOrDefaultAsync(cancel);
            if (exact is not null)
            {
                return exact;
            }

            string domain = MailAddresses.DomainOf(from);
            return await usable.Where(a => a.Address.EndsWith("@" + domain)).OrderBy(a => a.Role).ThenBy(a => a.Id).FirstOrDefaultAsync(cancel);
        }

        return null;
    }
}

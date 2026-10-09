using MailKit;
using MatMail.Data;
using MatMail.MailSync;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>"Synchronise now" for ordinary users: which accounts are theirs, and the run itself.</summary>
public class UserSyncTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddMailSync());
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>An account pointing at a closed port: it is a relevant account, and a run fails fast with "cannot connect".</summary>
    private async Task<MailAccount> AddAccountAsync(string name, Action<MailAccount>? configure = null, long? tenantId = null)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount
        {
            TenantId = tenantId ?? _seed.Tenant.Id,
            Name = name,
            Address = $"{name.Replace(' ', '-').ToLowerInvariant()}@elsewhere.test",
            ReceiveHost = "127.0.0.1",
            ReceivePort = 1,
            ReceiveSecurity = ConnectionSecurity.None,
            ReceiveUsername = "user",
        };
        configure?.Invoke(account);
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<string[]> RelevantForAsync(User user)
    {
        using IServiceScope scope = _host.ScopeAs(user);
        MailUser mailUser = scope.ServiceProvider.GetRequiredService<MailAccessService>().GetCurrentUser()!;
        return (await scope.ServiceProvider.GetRequiredService<UserSyncService>().FindRelevantAccountsAsync(mailUser)).Select(a => a.Name).OrderBy(n => n).ToArray();
    }

    private async Task GiveAccessAsync(User user, Mailbox mailbox, MailboxAccess access)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailboxPermissions.Add(new MailboxPermission { TenantId = mailbox.TenantId, MailboxId = mailbox.Id, UserId = user.Id, Access = access });
        await db.SaveChangesAsync();
    }

    [DbFact]
    public async Task The_relevant_accounts_are_those_that_feed_the_mailboxes_of_the_user()
    {
        await AddAccountAsync("Alice account", a => a.Address = "alice@example.test");                       // its address is an address of her mailbox
        await AddAccountAsync("Bob account", a => a.Address = "bob@example.test");                            // somebody else's
        await AddAccountAsync("Catch all", a => { a.Address = "catchall@example.test"; a.IsCatchAll = true; }); // a domain both have addresses in
        await AddAccountAsync("Foreign catch all", a => { a.Address = "catchall@other-domain.test"; a.IsCatchAll = true; });
        await AddAccountAsync("Feeds info", a => a.TargetMailboxId = _seed.Info.Id);                          // delivers into the shared mailbox
        await AddAccountAsync("Send only", a => { a.Address = "alice@example.test"; a.Role = MailAccountRole.SendOnly; });
        await AddAccountAsync("Disabled", a => { a.Address = "alice@example.test"; a.IsEnabled = false; });
        await AddAccountAsync("No server", a => { a.Address = "alice@example.test"; a.ReceiveProtocol = ReceiveProtocol.None; });
        await AddAccountAsync("Backup of Alice", a => { a.Role = MailAccountRole.Backup; a.TargetMailboxId = _seed.AliceMailbox.Id; });
        await AddAccountAsync("Backup of nobody", a => a.Role = MailAccountRole.Backup);
        long otherTenant = (await CreateTenantAsync("Other"));
        await AddAccountAsync("Alien", a => a.Address = "alice@example.test", tenantId: otherTenant);

        Assert.Equal(new[] { "Alice account", "Backup of Alice", "Catch all" }, await RelevantForAsync(_seed.Alice));
        Assert.Equal(new[] { "Bob account", "Catch all" }, await RelevantForAsync(_seed.Bob));

        // A delegated mailbox counts: the account that feeds it is relevant for whoever works in it.
        await GiveAccessAsync(_seed.Alice, _seed.Info, MailboxAccess.Read);
        Assert.Equal(new[] { "Alice account", "Backup of Alice", "Catch all", "Feeds info" }, await RelevantForAsync(_seed.Alice));
    }

    [DbFact]
    public async Task An_account_that_delivered_into_a_mailbox_before_is_relevant_for_its_users()
    {
        MailAccount past = await AddAccountAsync("Delivered before");
        await AddAccountAsync("Never delivered");
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverToMailboxAsync(
                _seed.AliceMailbox, RawMail.Build("a@x.test", "alice@example.test", "From the account", "text"), new DeliverySource { Account = past });
        }

        Assert.Equal(new[] { "Delivered before" }, await RelevantForAsync(_seed.Alice));
        Assert.Empty(await RelevantForAsync(_seed.Bob));
    }

    [DbFact]
    public async Task Synchronising_runs_every_relevant_account_and_counts_what_happened()
    {
        await AddAccountAsync("First", a => a.Address = "alice@example.test");
        await AddAccountAsync("Second", a => a.TargetMailboxId = _seed.AliceMailbox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice, Permissions.MailUse, Permissions.AccountsManage);
        MailUser user = scope.ServiceProvider.GetRequiredService<MailAccessService>().GetCurrentUser()!;
        UserSyncResult result = await scope.ServiceProvider.GetRequiredService<UserSyncService>().SyncAsync(user, TimeSpan.FromSeconds(30), showProblems: true);

        Assert.Equal(2, result.Accounts);
        Assert.Equal((0, 2, 0, 0), (result.Synced, result.Failed, result.AlreadyRunning, result.StillRunning));   // nothing listens on port 1
        Assert.Equal(2, result.Problems.Count);
        Assert.Contains(result.Problems, p => p.StartsWith("First: "));

        // Somebody without the right to administer accounts is told how many failed, not why (the reason names servers).
        UserSyncResult quiet = await scope.ServiceProvider.GetRequiredService<UserSyncService>().SyncAsync(user, TimeSpan.FromSeconds(30), showProblems: false);
        Assert.Equal(2, quiet.Failed);
        Assert.Empty(quiet.Problems);
    }

    [DbFact]
    public async Task A_user_without_accounts_gets_an_empty_answer_and_nothing_runs()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Bob);
        MailUser user = scope.ServiceProvider.GetRequiredService<MailAccessService>().GetCurrentUser()!;

        UserSyncResult result = await scope.ServiceProvider.GetRequiredService<UserSyncService>().SyncAsync(user, TimeSpan.FromSeconds(5), showProblems: false);

        Assert.Equal(UserSyncResult.None, result);
    }

    [DbFact]
    public async Task A_run_that_is_going_already_is_not_started_twice()
    {
        MailAccount account = await AddAccountAsync("Busy", a => a.Address = "alice@example.test");
        using (IServiceScope scope = _host.Scope())
        {
            // The persisted state of a run that is under way (it counts as stale only after half an hour).
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailAccounts.Where(a => a.Id == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSyncState, SyncState.Running).SetProperty(a => a.UpdateDate, DateTime.UtcNow));
        }

        using IServiceScope user = _host.ScopeAs(_seed.Alice);
        UserSyncResult result = await user.ServiceProvider.GetRequiredService<UserSyncService>().SyncAsync(
            user.ServiceProvider.GetRequiredService<MailAccessService>().GetCurrentUser()!, TimeSpan.FromSeconds(30), showProblems: false);

        Assert.Equal((1, 0, 0, 1), (result.Accounts, result.Synced, result.Failed, result.AlreadyRunning));
    }

    private async Task<long> CreateTenantAsync(string name)
    {
        using IServiceScope scope = _host.Scope();
        (Tenant? tenant, string? error) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync(name, null);
        Assert.Null(error);
        return tenant!.Id;
    }
}

/// <summary>"Synchronise now" against a real provider, and the rules that run on what it brings in.</summary>
public class UserSyncProviderTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddMailSync(o => o.BatchSize = 5));
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailAccount> AddAccountAsync(ProviderUser provider)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        (string imapHost, int imapPort) = TestProvider.Imap;
        var account = new MailAccount
        {
            TenantId = _seed.Tenant.Id,
            Name = "Provider " + provider.Login,
            Address = "alice@example.test",
            ReceiveProtocol = ReceiveProtocol.Imap,
            ReceiveHost = imapHost,
            ReceivePort = imapPort,
            ReceiveSecurity = ConnectionSecurity.None,
            ReceiveUsername = provider.Login,
            ReceivePasswordProtected = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect(provider.Password),
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<UserSyncResult> UserSyncAsync()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        return await scope.ServiceProvider.GetRequiredService<UserSyncService>().SyncAsync(
            scope.ServiceProvider.GetRequiredService<MailAccessService>().GetCurrentUser()!, TimeSpan.FromSeconds(60), showProblems: true);
    }

    [ProviderFact]
    public async Task New_mail_arrives_when_the_user_synchronises_and_the_transfer_is_logged()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Fresh", "one"));
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Fresher", "two"));
        MailAccount account = await AddAccountAsync(provider);

        UserSyncResult result = await UserSyncAsync();

        Assert.Equal((1, 1, 2, 0), (result.Accounts, result.Synced, result.Downloaded, result.Failed));
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal(new[] { "Fresh", "Fresher" }, await db.MailMessages.AsNoTracking().Where(m => m.MailboxId == _seed.AliceMailbox.Id).OrderBy(m => m.Id).Select(m => m.Subject).ToArrayAsync());

        List<MailTransfer> log = await db.MailTransfers.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, log.Count);
        Assert.All(log, t => Assert.Equal((TransferDirection.Inbound, TransferChannel.ProviderAccount, TransferStatus.Delivered, account.Name), (t.Direction, t.Channel, t.Status, t.Peer)));
        Assert.Equal("max@sender.test", log[0].Sender);
    }

    [ProviderFact]
    public async Task What_a_rule_does_to_fetched_mail_is_passed_on_to_the_provider()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        uint kept = await provider.AppendAsync("INBOX", RawMail.Build("news@shop.test", "alice@example.test", "Weekly", "one"));
        uint gone = await provider.AppendAsync("INBOX", RawMail.Build("spam@bad.test", "alice@example.test", "Win big", "two"));
        MailAccount account = await AddAccountAsync(provider);
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.MailRules.Add(new MailRule
            {
                TenantId = _seed.Tenant.Id, MailboxId = _seed.AliceMailbox.Id, Name = "Read the shop", Position = 0,
                Conditions = { new MailRuleCondition { TenantId = _seed.Tenant.Id, Field = RuleField.From, Operator = RuleOperator.Contains, Value = "shop.test" } },
                Actions = { new MailRuleAction { TenantId = _seed.Tenant.Id, Type = RuleActionType.MarkAsRead } },
            });
            db.MailRules.Add(new MailRule
            {
                TenantId = _seed.Tenant.Id, MailboxId = _seed.AliceMailbox.Id, Name = "Delete spam", Position = 1,
                Conditions = { new MailRuleCondition { TenantId = _seed.Tenant.Id, Field = RuleField.From, Operator = RuleOperator.Contains, Value = "bad.test" } },
                Actions = { new MailRuleAction { TenantId = _seed.Tenant.Id, Type = RuleActionType.Discard } },
            });
            await db.SaveChangesAsync();
        }

        UserSyncResult first = await UserSyncAsync();
        Assert.Equal(2, first.Downloaded);   // both were fetched; one was deleted on arrival

        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            MailMessage stored = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.AliceMailbox.Id);
            Assert.Equal("Weekly", stored.Subject);
            Assert.True(stored.IsRead);
        }

        // The next run compares the flags: the provider takes over what the rule did instead of undoing it.
        await UserSyncAsync();
        Assert.True((await provider.FlagsAsync("INBOX", kept)).HasFlag(MessageFlags.Seen));

        using IServiceScope check = _host.Scope();
        var checkDb = check.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.True((await checkDb.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.AliceMailbox.Id)).IsRead);
        Assert.Equal(2, await provider.CountAsync("INBOX"));   // the deleted one is still at the provider (keep on server), and was not fetched again
        Assert.Equal(1, await checkDb.MailMessages.CountAsync(m => m.MailboxId == _seed.AliceMailbox.Id));
        Assert.Equal(2, await checkDb.RemoteMessageStates.CountAsync(r => r.MailAccountId == account.Id));
        _ = gone;
    }
}

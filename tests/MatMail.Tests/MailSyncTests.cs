using MailKit;
using MatMail.Data;
using MatMail.MailSync;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MailStore = MatMail.Messaging.MailStore;

namespace MatMail.Tests;

// ---------------------------------------------------------------------------------------------------------------------
// Pure logic (no server, no database)
// ---------------------------------------------------------------------------------------------------------------------

public class MailSyncLogicTests
{
    [Theory]
    [InlineData("INBOX", "INBOX", FolderAttributes.None, FolderKind.Inbox)]
    [InlineData("Sent", "Sent", FolderAttributes.None, FolderKind.Sent)]
    [InlineData("Gesendete Elemente", "Gesendete Elemente", FolderAttributes.None, FolderKind.Sent)]
    [InlineData("INBOX.Gesendet", "Gesendet", FolderAttributes.None, FolderKind.Sent)]
    [InlineData("Entwürfe", "Entwürfe", FolderAttributes.None, FolderKind.Drafts)]
    [InlineData("Papierkorb", "Papierkorb", FolderAttributes.None, FolderKind.Trash)]
    [InlineData("Gelöschte Elemente", "Gelöschte Elemente", FolderAttributes.None, FolderKind.Trash)]
    [InlineData("Spam", "Spam", FolderAttributes.None, FolderKind.Junk)]
    [InlineData("Archiv", "Archiv", FolderAttributes.None, FolderKind.Archive)]
    [InlineData("Postausgang-Kopien", "Postausgang-Kopien", FolderAttributes.Sent, FolderKind.Sent)]
    [InlineData("[Gmail].Papierkorb", "Papierkorb", FolderAttributes.Trash, FolderKind.Trash)]
    [InlineData("Projects.Sent", "Sent", FolderAttributes.None, FolderKind.Custom)]
    [InlineData("Projects", "Projects", FolderAttributes.None, FolderKind.Custom)]
    public void Remote_folders_map_to_local_special_folders_by_attribute_or_common_name(string fullName, string name, FolderAttributes attributes, FolderKind expected)
        => Assert.Equal(expected, RemoteFolderMap.KindOf(new RemoteFolderInfo(fullName, name, '.', attributes)));

    [Fact]
    public void Remote_paths_become_local_paths_with_slashes()
    {
        Assert.Equal("INBOX/Projects/2026", RemoteFolderMap.LocalPathOf(new RemoteFolderInfo("INBOX.Projects.2026", "2026", '.', FolderAttributes.None)));
        Assert.Equal("INBOX/Rechnungen", RemoteFolderMap.LocalPathOf(new RemoteFolderInfo("Inbox/Rechnungen", "Rechnungen", '/', FolderAttributes.None)));
        Assert.Equal("Kunden-Lieferanten/A", RemoteFolderMap.LocalPathOf(new RemoteFolderInfo("Kunden/Lieferanten.A", "A", '.', FolderAttributes.None)));
        Assert.Equal("Backup/Strato - info@example.test/INBOX", RemoteFolderMap.BackupPathOf("Strato / info@example.test", new RemoteFolderInfo("INBOX", "INBOX", '.', FolderAttributes.None)));
        Assert.Equal("_", RemoteFolderMap.SanitizeSegment(".."));
        Assert.Equal(200, RemoteFolderMap.SanitizeSegment(new string('x', 300)).Length);
    }

    [Fact]
    public void Everyday_mail_with_all_folders_takes_only_incoming_folders()
    {
        Assert.True(RemoteFolderMap.IsIncomingFolder(new RemoteFolderInfo("INBOX", "INBOX", '/', FolderAttributes.None)));
        Assert.True(RemoteFolderMap.IsIncomingFolder(new RemoteFolderInfo("Newsletter", "Newsletter", '/', FolderAttributes.None)));
        Assert.False(RemoteFolderMap.IsIncomingFolder(new RemoteFolderInfo("Gesendet", "Gesendet", '/', FolderAttributes.None)));
        Assert.False(RemoteFolderMap.IsIncomingFolder(new RemoteFolderInfo("Spam", "Spam", '/', FolderAttributes.None)));
        Assert.False(RemoteFolderMap.IsIncomingFolder(new RemoteFolderInfo("[Gmail]/All Mail", "All Mail", '/', FolderAttributes.All)));
        Assert.False(RemoteFolderMap.IsCopiedFolder(new RemoteFolderInfo("[Gmail]/Starred", "Starred", '/', FolderAttributes.Flagged)));
        Assert.True(RemoteFolderMap.IsCopiedFolder(new RemoteFolderInfo("Gesendet", "Gesendet", '/', FolderAttributes.None)));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(40, 60)]
    public void Failures_back_off_exponentially_up_to_an_hour(int failures, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SyncSchedule.BackoffAfter(failures, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60)));

    [Fact]
    public void Long_intervals_are_never_shortened_by_the_back_off()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), SyncSchedule.BackoffAfter(3, TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(60)));

        DateTime now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan interval = TimeSpan.FromMinutes(5);
        TimeSpan max = TimeSpan.FromMinutes(60);
        Assert.Equal(now.AddMinutes(5), SyncSchedule.NextSyncDate(now, interval, false, 0, false, max));
        Assert.Equal(now, SyncSchedule.NextSyncDate(now, interval, false, 0, true, max));
        Assert.Equal(now.AddMinutes(20), SyncSchedule.NextSyncDate(now, interval, true, 3, true, max));
    }

    [Fact]
    public void New_uids_are_filtered_sorted_and_batched()
    {
        // "UID 8:*" also returns the highest message (UID 7) when nothing is above 7; it must not come back.
        Assert.Empty(SyncBatches.NewUids(new uint[] { 7 }, 7, 50));

        List<uint[]> batches = SyncBatches.NewUids(new uint[] { 12, 9, 15, 9, 11, 3 }, 8, 2);
        Assert.Equal(new[] { new uint[] { 9, 11 }, new uint[] { 12, 15 } }, batches);
    }

    [Fact]
    public void The_folder_position_stops_at_the_first_failure()
    {
        var progress = new UidProgress(10);
        progress.Complete(11);
        progress.Complete(12);
        progress.Fail();
        progress.Complete(14);
        Assert.Equal(12, progress.Position);
        Assert.True(progress.Blocked);
    }

    [Fact]
    public void Flags_follow_the_side_that_changed()
    {
        DateTime synced = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var local = new[]
        {
            new LocalFlags(1, "1", IsRead: false, IsStarred: false, UpdateDate: synced.AddMinutes(-1), SyncedDate: synced), // remote read it
            new LocalFlags(2, "2", IsRead: true, IsStarred: true, UpdateDate: synced.AddMinutes(1), SyncedDate: synced),    // changed here
            new LocalFlags(3, "3", IsRead: true, IsStarred: false, UpdateDate: synced.AddMinutes(1), SyncedDate: synced),   // moved, equal
            new LocalFlags(4, "4", IsRead: false, IsStarred: false, UpdateDate: synced, SyncedDate: synced),                // nothing
            new LocalFlags(5, "5", IsRead: false, IsStarred: false, UpdateDate: synced, SyncedDate: synced),                // two copies
            new LocalFlags(6, "5", IsRead: false, IsStarred: false, UpdateDate: synced, SyncedDate: synced),
        };
        var remote = new Dictionary<string, RemoteFlags>
        {
            ["1"] = new(Seen: true, Flagged: false),
            ["2"] = new(Seen: false, Flagged: false),
            ["3"] = new(Seen: true, Flagged: false),
            ["4"] = new(Seen: false, Flagged: false),
            ["5"] = new(Seen: true, Flagged: true),
        };

        FlagPlan plan = FlagSync.Plan(local, remote);

        FlagPull pull = Assert.Single(plan.Pull);
        Assert.Equal((1L, true, false), (pull.MessageId, pull.IsRead, pull.IsStarred));
        FlagPush push = Assert.Single(plan.Push);
        Assert.Equal(("2", true, true), (push.RemoteUid, push.Seen, push.Flagged));
        Assert.Equal(new[] { "3" }, plan.InSync);
    }

    [Fact]
    public void A_live_stub_keeps_only_the_listing_headers()
    {
        HeaderList headers = HeaderList.Load(new MemoryStream(RawMail.Build(
            "Max <max@sender.test>", "alice@example.test", "Live subject", "body", "<live@sender.test>",
            "Delivered-To: bob@example.test\r\nReceived: from x by y; Tue, 07 Oct 2026 10:00:00 +0200\r\nX-Spam-Score: 1.0\r\n")));

        byte[] stub = LiveStub.Build(headers);
        string text = System.Text.Encoding.UTF8.GetString(stub);

        Assert.EndsWith("\r\n\r\n", text);
        Assert.DoesNotContain("X-Spam-Score", text);
        Assert.DoesNotContain("Received:", text);
        ParsedMessage parsed = MessageParser.Parse(stub);
        Assert.Equal("Live subject", parsed.Subject);
        Assert.Equal("max@sender.test", parsed.FromAddress);
        Assert.Equal("<live@sender.test>", parsed.MessageId);
        Assert.Equal(new[] { "bob@example.test" }, parsed.DeliveredTo);
        Assert.Equal(string.Empty, parsed.Preview);
    }

    [Fact]
    public void The_live_cache_is_bounded_and_forgets_old_entries()
    {
        var cache = new LiveMessageCache(maxBytes: 400, lifetime: TimeSpan.FromMinutes(1));
        cache.Add(1, new byte[100]);
        cache.Add(2, new byte[100]);
        cache.Add(3, new byte[100]);
        Assert.True(cache.TryGet(1, out _));
        cache.Add(4, new byte[100]);
        cache.Add(5, new byte[100]);

        // 2 was the least recently used one; 1 was read in between.
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(1, out byte[]? raw));
        Assert.Equal(100, raw!.Length);
        Assert.Equal(400, cache.Bytes);

        cache.Add(6, new byte[101]);
        Assert.False(cache.TryGet(6, out _));

        var expiring = new LiveMessageCache(1000, TimeSpan.Zero);
        expiring.Add(1, new byte[10]);
        Assert.False(expiring.TryGet(1, out _));
    }

    [Fact]
    public void Pop3_messages_get_their_date_from_the_newest_received_header()
    {
        byte[] raw = RawMail.Build("a@x.test", "b@y.test", "Hi", "x", extraHeaders:
            "Received: from relay by mx.provider.test; Wed, 08 Oct 2026 06:30:00 +0000\r\nReceived: from client by relay; Wed, 08 Oct 2026 06:29:00 +0000\r\n");
        Assert.Equal(new DateTime(2026, 10, 8, 6, 30, 0, DateTimeKind.Utc), MessageDates.ReceivedDateOf(raw));

        byte[] withoutReceived = RawMail.Build("a@x.test", "b@y.test", "Hi", "x");
        Assert.Equal(new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc), MessageDates.ReceivedDateOf(withoutReceived));
    }

    [Fact]
    public void Retention_settings_that_cannot_apply_are_replaced_and_explained()
    {
        var backup = new MailAccount { Role = MailAccountRole.Backup, Retention = ServerRetention.DeleteAfterDownload };
        Assert.Equal(ServerRetention.KeepOnServer, SyncRun.EffectiveRetention(backup, out string? backupNote));
        Assert.Contains("never delete", backupNote);

        var pop = new MailAccount { ReceiveProtocol = ReceiveProtocol.Pop3, Retention = ServerRetention.LiveAccess };
        Assert.Equal(ServerRetention.KeepOnServer, SyncRun.EffectiveRetention(pop, out string? popNote));
        Assert.Contains("IMAP", popNote);

        var mail = new MailAccount { Retention = ServerRetention.DeleteAfterDownload };
        Assert.Equal(ServerRetention.DeleteAfterDownload, SyncRun.EffectiveRetention(mail, out string? none));
        Assert.Null(none);
    }

    [Fact]
    public void Run_summaries_read_naturally()
    {
        var options = new MailSyncOptions();
        var run = new SyncRun(new MailAccount(), options) { Downloaded = 12, DeletedAtProvider = 12 };
        Assert.Equal("12 new messages; 12 deleted at the provider", SyncText.Summary(run, 3));
        Assert.Equal("1 new message", SyncText.Summary(new SyncRun(new MailAccount(), options) { Downloaded = 1 }, 3));
        Assert.Equal("No new messages", SyncText.Summary(new SyncRun(new MailAccount(), options), 3));

        var failed = new SyncRun(new MailAccount(), options);
        failed.FolderErrors.Add("folder News does not exist at the provider");
        Assert.Equal(
            "mail.example.test: the user name or password was refused.; folder News does not exist at the provider",
            SyncText.Problem(failed, new MailKit.Security.AuthenticationException(), "mail.example.test"));
    }

    [Fact]
    public void The_module_wires_up_like_the_application_does()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddMatMailServices(new MatMail.Configuration.AppConfig());
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMailSync();

        // ASP.NET Core validates like this in Development: every service resolvable, no singleton depending on a scoped one.
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.IsType<RemoteContentFetcher>(provider.GetRequiredService<IRemoteContentProvider>());
        Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is MailSyncService);
        Assert.Same(provider.GetRequiredService<MailSyncService>(), provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MailSyncService>().Single());
    }

    [Fact]
    public void Only_problems_of_the_message_itself_count_against_it()
    {
        Assert.True(SyncImporter.IsMessageProblem(new FormatException("broken MIME")));
        Assert.True(SyncImporter.IsMessageProblem(new DbUpdateException("value too long", new InvalidOperationException())));
        Assert.False(SyncImporter.IsMessageProblem(new IOException("connection reset")));
        Assert.False(SyncImporter.IsMessageProblem(new ServiceNotConnectedException()));
        Assert.False(SyncImporter.IsMessageProblem(new InvalidOperationException("wrapped", new System.Net.Sockets.SocketException())));
        Assert.False(SyncImporter.IsMessageProblem(new OperationCanceledException()));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Scheduler and runner without a provider (database only)
// ---------------------------------------------------------------------------------------------------------------------

public class MailSyncSchedulerTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddMailSync(o => o.TickInterval = TimeSpan.FromHours(1)));
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MailSyncService Scheduler => _host.Services.GetRequiredService<MailSyncService>();

    /// <summary>An IMAP account pointing at a closed local port: every run fails fast with "cannot connect".</summary>
    private async Task<MailAccount> AddAccountAsync(string name, Action<MailAccount>? configure = null, long? tenantId = null)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount
        {
            TenantId = tenantId ?? _seed.Tenant.Id,
            Name = name,
            Address = "alice@example.test",
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

    private async Task SetUpdateDateAsync(long accountId, DateTime updateDate)
    {
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailAccounts.Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdateDate, updateDate));
    }

    private async Task<MailAccount> ReloadAsync(long accountId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
    }

    [DbFact]
    public async Task The_scheduler_only_picks_accounts_that_are_due()
    {
        DateTime now = DateTime.UtcNow;
        MailAccount due = await AddAccountAsync("due", a => a.NextSyncDate = now.AddMinutes(-1));
        MailAccount never = await AddAccountAsync("never synchronised");
        await AddAccountAsync("not due yet", a => a.NextSyncDate = now.AddMinutes(3));
        await AddAccountAsync("disabled", a => a.IsEnabled = false);
        await AddAccountAsync("send only", a => a.Role = MailAccountRole.SendOnly);
        await AddAccountAsync("no protocol", a => a.ReceiveProtocol = ReceiveProtocol.None);
        await AddAccountAsync("no host", a => a.ReceiveHost = null);
        MailAccount running = await AddAccountAsync("running", a => a.LastSyncState = SyncState.Running);
        MailAccount crashed = await AddAccountAsync("crashed run", a => { a.LastSyncState = SyncState.Running; a.NextSyncDate = now.AddMinutes(-2); });
        await SetUpdateDateAsync(running.Id, now.AddMinutes(-5));
        await SetUpdateDateAsync(crashed.Id, now.AddMinutes(-31));

        long inactiveTenant;
        using (IServiceScope scope = _host.Scope())
        {
            (Tenant? other, _) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Gone GmbH", null);
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == other!.Id)).IsActive = false;
            await db.SaveChangesAsync();
            inactiveTenant = other!.Id;
        }

        await AddAccountAsync("inactive tenant", tenantId: inactiveTenant);

        IReadOnlyList<long> found = await Scheduler.FindDueAccountsAsync(now, 10, Array.Empty<long>());
        Assert.Equal(new[] { never.Id, crashed.Id, due.Id }, found);

        Assert.Equal(new[] { never.Id }, await Scheduler.FindDueAccountsAsync(now, 1, Array.Empty<long>()));
        Assert.Equal(new[] { crashed.Id, due.Id }, await Scheduler.FindDueAccountsAsync(now, 10, new[] { never.Id }));
    }

    [DbFact]
    public async Task Accounts_that_do_not_fetch_mail_say_why()
    {
        var trigger = _host.Services.GetRequiredService<MailSyncTrigger>();
        MailAccount disabled = await AddAccountAsync("disabled", a => a.IsEnabled = false);
        MailAccount sendOnly = await AddAccountAsync("send only", a => a.Role = MailAccountRole.SendOnly);

        SyncReport first = await trigger.SyncNowAsync(disabled.Id);
        SyncReport second = await trigger.SyncNowAsync(sendOnly.Id);
        SyncReport third = await trigger.SyncNowAsync(987654);

        Assert.False(first.Started);
        Assert.Equal("The account is disabled.", first.Message);
        Assert.Equal("The account only sends mail; nothing is fetched.", second.Message);
        Assert.Equal("The account does not exist.", third.Message);
        Assert.Equal(SyncState.Never, (await ReloadAsync(disabled.Id)).LastSyncState);
    }

    [DbFact]
    public async Task A_run_that_is_still_going_is_not_started_twice()
    {
        MailAccount account = await AddAccountAsync("busy", a => a.LastSyncState = SyncState.Running);

        SyncReport report = await _host.Services.GetRequiredService<MailSyncTrigger>().SyncNowAsync(account.Id);

        Assert.False(report.Started);
        Assert.Contains("already running", report.Message);
    }

    [DbFact]
    public async Task A_requested_account_runs_first_and_an_unreachable_server_is_reported()
    {
        MailAccount account = await AddAccountAsync("unreachable", a => a.NextSyncDate = DateTime.UtcNow.AddHours(1));
        var trigger = _host.Services.GetRequiredService<MailSyncTrigger>();

        Assert.Empty(await Scheduler.StartDueRunsAsync(DateTime.UtcNow, CancellationToken.None));
        trigger.RequestSync(account.Id);
        Assert.True(trigger.IsRequested(account.Id));
        Assert.Equal(new[] { account.Id }, await Scheduler.StartDueRunsAsync(DateTime.UtcNow, CancellationToken.None));
        await Scheduler.WhenIdleAsync();

        MailAccount reloaded = await ReloadAsync(account.Id);
        Assert.Equal(SyncState.Error, reloaded.LastSyncState);
        Assert.StartsWith("127.0.0.1: cannot connect", reloaded.LastSyncMessage);
        Assert.Equal(1, reloaded.FailureCount);
        Assert.False(trigger.IsRequested(account.Id));
    }

    [DbFact]
    public async Task A_reset_forgets_what_was_fetched_but_not_while_a_run_is_going()
    {
        var trigger = _host.Services.GetRequiredService<MailSyncTrigger>();
        MailAccount account = await AddAccountAsync("moved to another server", a => { a.NextSyncDate = DateTime.UtcNow.AddMinutes(5); a.FailureCount = 2; });
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.MailAccountFolderStates.Add(new MailAccountFolderState { MailAccountId = account.Id, RemoteFolder = "INBOX", UidValidity = 7, LastUid = 42 });
            db.RemoteMessageStates.Add(new RemoteMessageState { MailAccountId = account.Id, RemoteFolder = "INBOX", RemoteUid = "42" });
            await db.SaveChangesAsync();
        }

        Assert.True(await trigger.ResetAsync(account.Id));

        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Assert.False(await db.MailAccountFolderStates.AnyAsync(s => s.MailAccountId == account.Id));
            Assert.False(await db.RemoteMessageStates.AnyAsync(r => r.MailAccountId == account.Id));
        }

        MailAccount reset = await ReloadAsync(account.Id);
        Assert.Null(reset.NextSyncDate);
        Assert.Equal(0, reset.FailureCount);

        MailAccount busy = await AddAccountAsync("busy", a => a.LastSyncState = SyncState.Running);
        Assert.False(await trigger.ResetAsync(busy.Id));
    }

    [DbFact]
    public async Task The_background_service_wakes_up_for_a_request()
    {
        MailAccount account = await AddAccountAsync("requested", a => a.NextSyncDate = DateTime.UtcNow.AddHours(1));
        using var stop = new CancellationTokenSource();
        await Scheduler.StartAsync(stop.Token);
        try
        {
            // The tick is an hour: only the request can start the run.
            await Task.Delay(300);
            _host.Services.GetRequiredService<MailSyncTrigger>().RequestSync(account.Id);

            DateTime giveUp = DateTime.UtcNow.AddSeconds(30);
            while ((await ReloadAsync(account.Id)).LastSyncState is SyncState.Never or SyncState.Running && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(200);
            }
        }
        finally
        {
            await Scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(SyncState.Error, (await ReloadAsync(account.Id)).LastSyncState);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Against a real provider (GreenMail, see Support/TestProvider.cs)
// ---------------------------------------------------------------------------------------------------------------------

public class MailSyncProviderTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services => services.AddMailSync(o => o.BatchSize = 2));
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ----- helpers ------------------------------------------------------------------------------------------------

    private async Task<MailAccount> AddAccountAsync(ProviderUser provider, Action<MailAccount>? configure = null, TestHost? host = null, string? password = null)
    {
        host ??= _host;
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long tenantId = await db.Tenants.Where(t => t.Name == "Home").Select(t => t.Id).SingleAsync();
        (string imapHost, int imapPort) = TestProvider.Imap;
        var account = new MailAccount
        {
            TenantId = tenantId,
            Name = "Provider " + provider.Login,
            Address = "alice@example.test",
            ReceiveProtocol = ReceiveProtocol.Imap,
            ReceiveHost = imapHost,
            ReceivePort = imapPort,
            ReceiveSecurity = ConnectionSecurity.None,
            ReceiveUsername = provider.Login,
            ReceivePasswordProtected = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect(password ?? provider.Password),
            SyncIntervalMinutes = 5,
        };
        configure?.Invoke(account);
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private Task<SyncReport> SyncAsync(MailAccount account, TestHost? host = null)
        => (host ?? _host).Services.GetRequiredService<MailSyncTrigger>().SyncNowAsync(account.Id);

    private async Task<T> QueryAsync<T>(Func<MatMailDbContext, Task<T>> query, TestHost? host = null)
    {
        using IServiceScope scope = (host ?? _host).Scope();
        return await query(scope.ServiceProvider.GetRequiredService<MatMailDbContext>());
    }

    private Task<List<MailMessage>> InboxAsync(long mailboxId)
        => QueryAsync(db => db.MailMessages.AsNoTracking().Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == FolderKind.Inbox).OrderBy(m => m.Uid).ToListAsync());

    private Task<List<MailMessage>> FolderAsync(long mailboxId, FolderKind kind)
        => QueryAsync(db => db.MailMessages.AsNoTracking().Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == kind).OrderBy(m => m.Uid).ToListAsync());

    private async Task<List<MailMessage>> FolderAsync(long mailboxId, string path)
    {
        using IServiceScope scope = _host.Scope();
        FolderInfo? folder = await scope.ServiceProvider.GetRequiredService<FolderService>().FindByPathAsync(mailboxId, path);
        Assert.True(folder is not null, $"Folder {path} is missing.");
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.AsNoTracking()
            .Where(m => m.FolderId == folder.Id).OrderBy(m => m.Uid).ToListAsync();
    }

    private Task<MailAccount> AccountAsync(long id, TestHost? host = null)
        => QueryAsync(db => db.MailAccounts.AsNoTracking().SingleAsync(a => a.Id == id), host);

    private Task<List<RemoteMessageState>> RecordsAsync(long accountId, TestHost? host = null)
        => QueryAsync(db => db.RemoteMessageStates.AsNoTracking().Where(r => r.MailAccountId == accountId).OrderBy(r => r.Id).ToListAsync(), host);

    private Task<List<ActivityLog>> SyncLogAsync(TestHost? host = null)
        => QueryAsync(db => db.ActivityLogs.AsNoTracking().Where(l => l.Category == ActivityCategory.Sync).OrderBy(l => l.Id).ToListAsync(), host);

    private async Task<Mailbox> CreateMailboxAsync(string name)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailboxService>().CreateMailboxAsync(name, MailboxType.Shared, null, _seed.Tenant.Id);
    }

    // ----- everyday mail --------------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task New_mail_is_fetched_once_routed_by_recipient_and_kept_on_the_server()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "First", "one"), MessageFlags.Seen, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Second", "two"), MessageFlags.Flagged);
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Third", "three"));
        MailAccount account = await AddAccountAsync(provider);

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(3, report.Downloaded);
        Assert.Equal("3 new messages", report.Message);
        List<MailMessage> inbox = await InboxAsync(_seed.AliceMailbox.Id);
        Assert.Equal(new[] { "First", "Second", "Third" }, inbox.Select(m => m.Subject));
        Assert.True(inbox[0].IsRead);
        Assert.True(inbox[1].IsStarred);
        Assert.False(inbox[2].IsRead);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), inbox[0].ReceivedDate);
        Assert.All(inbox, m => Assert.Equal((account.Id, "INBOX"), (m.SourceAccountId!.Value, m.RemoteFolder!)));

        MailAccount state = await AccountAsync(account.Id);
        Assert.Equal(SyncState.Ok, state.LastSyncState);
        Assert.Equal("3 new messages", state.LastSyncMessage);
        Assert.Equal(0, state.FailureCount);
        Assert.InRange(state.NextSyncDate!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
        Assert.Equal(3, (await RecordsAsync(account.Id)).Count(r => r.LocalMessageId is not null));
        MailAccountFolderState folder = await QueryAsync(db => db.MailAccountFolderStates.AsNoTracking().SingleAsync(s => s.MailAccountId == account.Id));
        Assert.Equal(3, folder.LastUid);
        Assert.Equal(await provider.UidValidityAsync("INBOX"), (uint)folder.UidValidity);

        // Keep on server: the next run downloads nothing and the provider still has everything.
        SyncReport second = await SyncAsync(account);
        Assert.Equal(0, second.Downloaded);
        Assert.Equal("No new messages", second.Message);
        Assert.Equal(3, await provider.CountAsync("INBOX"));
        Assert.Equal(3, (await InboxAsync(_seed.AliceMailbox.Id)).Count);

        // One activity line for the run that fetched something, none for the empty one.
        ActivityLog line = Assert.Single(await SyncLogAsync());
        Assert.Equal($"{account.Name}: 3 new messages", line.Message);
        Assert.Equal(_seed.Tenant.Id, line.TenantId);
    }

    [ProviderFact]
    public async Task Mail_that_reached_the_provider_by_smtp_is_fetched_for_the_accounts_address()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.SendAsync(RawMail.Build("max@sender.test", "bob@example.test", "Via SMTP", "Hello Bob"));
        Assert.Equal(1, await provider.CountAsync("INBOX"));
        MailAccount account = await AddAccountAsync(provider, a => a.Address = "bob@example.test");

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        MailMessage message = Assert.Single(await InboxAsync(_seed.BobMailbox.Id));
        Assert.Equal("Via SMTP", message.Subject);
        Assert.Equal("bob@example.test", message.EnvelopeRecipients);
    }

    [ProviderFact]
    public async Task A_catch_all_account_routes_by_the_delivered_to_header()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "team@elsewhere.test", "For Bob", "x", extraHeaders: "Delivered-To: bob@example.test\r\n"));
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "team@elsewhere.test", "For info", "x", extraHeaders: "Delivered-To: info@example.test\r\n"));
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "team@elsewhere.test", "For nobody", "x", extraHeaders: "X-Original-To: nobody@example.test\r\n"));
        MailAccount account = await AddAccountAsync(provider, a => { a.Address = "catchall@example.test"; a.IsCatchAll = true; });

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal("For Bob", Assert.Single(await InboxAsync(_seed.BobMailbox.Id)).Subject);
        Assert.Equal("For info", Assert.Single(await InboxAsync(_seed.Info.Id)).Subject);
        MailMessage unassigned = Assert.Single(await InboxAsync(_seed.UnassignedMailboxId));
        Assert.Equal(("For nobody", "nobody@example.test"), (unassigned.Subject, unassigned.EnvelopeRecipients));
        Assert.Empty(await InboxAsync(_seed.AliceMailbox.Id));
    }

    [ProviderFact]
    public async Task A_plain_account_whose_address_nobody_has_delivers_to_unassigned()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("friend@sender.test", "max.mueller@example.test", "For Max", "x"));
        MailAccount account = await AddAccountAsync(provider, a => a.Address = "max.mueller@example.test");

        await SyncAsync(account);

        MailMessage message = Assert.Single(await InboxAsync(_seed.UnassignedMailboxId));
        Assert.Equal("max.mueller@example.test", message.EnvelopeRecipients);
    }

    [ProviderFact]
    public async Task Unclaimed_mail_goes_to_the_accounts_fallback_mailbox()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("friend@sender.test", "max.mueller@example.test", "For Max", "x"));
        MailAccount account = await AddAccountAsync(provider, a => { a.Address = "max.mueller@example.test"; a.TargetMailboxId = _seed.Info.Id; });

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal("For Max", Assert.Single(await InboxAsync(_seed.Info.Id)).Subject);
        Assert.Empty(await InboxAsync(_seed.UnassignedMailboxId));
        RemoteMessageState record = Assert.Single(await RecordsAsync(account.Id));
        Assert.Equal(_seed.Info.Id, (await QueryAsync(db => db.MailMessages.AsNoTracking().SingleAsync(m => m.Id == record.LocalMessageId))).MailboxId);
    }

    [ProviderFact]
    public async Task Delete_after_download_empties_the_provider_once_the_mail_is_stored()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        for (int i = 1; i <= 5; i++)
        {
            await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", $"Mail {i}", "x"));
        }

        MailAccount account = await AddAccountAsync(provider, a => a.Retention = ServerRetention.DeleteAfterDownload);

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal((5, 5), (report.Downloaded, report.DeletedAtProvider));
        Assert.Equal("5 new messages; 5 deleted at the provider", report.Message);
        Assert.Equal(0, await provider.CountAsync("INBOX"));
        Assert.Equal(5, (await InboxAsync(_seed.AliceMailbox.Id)).Count);

        // The next run finds nothing and drops the records of the deleted originals.
        SyncReport second = await SyncAsync(account);
        Assert.Equal((0, 0), (second.Downloaded, second.DeletedAtProvider));
        Assert.Empty(await RecordsAsync(account.Id));
        Assert.Equal(5, (await InboxAsync(_seed.AliceMailbox.Id)).Count);
    }

    // ----- a mailbox that is full ------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task A_full_mailbox_ends_the_run_without_losing_mail_and_the_next_run_goes_on_when_there_is_room()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        for (int i = 1; i <= 4; i++)
        {
            await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", $"Mail {i}", new string('x', 1000)));
        }

        // Room for two of them (about 1.2 KB each): the third finds the mailbox full. Delete after download is where mail could get lost.
        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, 2000);
        MailAccount account = await AddAccountAsync(provider, a => a.Retention = ServerRetention.DeleteAfterDownload);

        SyncReport report = await SyncAsync(account);

        Assert.False(report.Succeeded);
        Assert.Equal((2, 0), (report.Downloaded, report.Failed));   // the mailbox is to blame, not the messages: none is counted towards giving up
        Assert.Contains("is full", report.Message);
        Assert.Equal(new[] { "Mail 1", "Mail 2" }, (await InboxAsync(_seed.AliceMailbox.Id)).Select(m => m.Subject));
        Assert.Equal(2, await provider.CountAsync("INBOX"));        // the other two are still there
        MailAccount state = await AccountAsync(account.Id);
        Assert.Equal(SyncState.Error, state.LastSyncState);
        Assert.Contains("is full", state.LastSyncMessage);

        // Still full: nothing happens, and still nothing is lost.
        SyncReport again = await SyncAsync(account);
        Assert.False(again.Succeeded);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(2, await provider.CountAsync("INBOX"));

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, null);
        SyncReport recovered = await SyncAsync(account);

        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal(2, recovered.Downloaded);
        Assert.Equal(new[] { "Mail 1", "Mail 2", "Mail 3", "Mail 4" }, (await InboxAsync(_seed.AliceMailbox.Id)).Select(m => m.Subject));
        Assert.Equal(0, await provider.CountAsync("INBOX"));
        Assert.Equal(SyncState.Ok, (await AccountAsync(account.Id)).LastSyncState);
    }

    [ProviderFact]
    public async Task A_backup_into_a_full_mailbox_stops_and_continues_later()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        for (int i = 1; i <= 3; i++)
        {
            await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", $"Mail {i}", new string('x', 1000)));
        }

        Mailbox backup = await CreateMailboxAsync("Backups");
        await QuotaTestSupport.SetLimitAsync(_host, backup.Id, 2000);
        MailAccount account = await AddAccountAsync(provider, a => { a.Name = "Strato"; a.Role = MailAccountRole.Backup; a.TargetMailboxId = backup.Id; });

        SyncReport report = await SyncAsync(account);

        Assert.False(report.Succeeded);
        Assert.Equal(2, report.Downloaded);
        Assert.Contains("is full", report.Message);
        Assert.Equal(3, await provider.CountAsync("INBOX"));   // a backup never deletes at the provider

        await QuotaTestSupport.SetLimitAsync(_host, backup.Id, null);
        SyncReport recovered = await SyncAsync(account);

        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal(new[] { "Mail 1", "Mail 2", "Mail 3" }, (await FolderAsync(backup.Id, "Backup/Strato/INBOX")).Select(m => m.Subject));
    }

    [ProviderFact]
    public async Task Unclaimed_mail_waits_in_unassigned_when_the_fallback_mailbox_is_full()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("friend@sender.test", "max.mueller@example.test", "For Max", "x"));
        await QuotaTestSupport.FillUpAsync(_host, _seed.Info.Id);
        MailAccount account = await AddAccountAsync(provider, a => { a.Address = "max.mueller@example.test"; a.TargetMailboxId = _seed.Info.Id; });

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);   // the mail is not lost and not refused: an administrator sees it in Unassigned
        Assert.Equal("For Max", Assert.Single(await InboxAsync(_seed.UnassignedMailboxId)).Subject);
        Assert.Single(await InboxAsync(_seed.Info.Id));   // the filler only
    }

    [ProviderFact]
    public async Task Only_the_configured_folders_are_fetched_and_a_missing_one_is_reported()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.CreateFolderAsync("Newsletter");
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "In the inbox", "x"));
        await provider.AppendAsync("Newsletter", RawMail.Build("news@sender.test", "alice@example.test", "Weekly news", "x"));
        MailAccount account = await AddAccountAsync(provider, a => a.SyncFolders = new[] { "Newsletter", "Does/Not/Exist" });

        SyncReport report = await SyncAsync(account);

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Downloaded);
        Assert.Equal("Weekly news", Assert.Single(await InboxAsync(_seed.AliceMailbox.Id)).Subject);
        MailAccount state = await AccountAsync(account.Id);
        Assert.Equal(SyncState.Error, state.LastSyncState);
        Assert.Equal("folder Does/Not/Exist does not exist at the provider (1 new message)", state.LastSyncMessage);

        // One folder worked, so there is no back-off: the next run comes after the normal interval.
        Assert.InRange(state.NextSyncDate!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
    }

    [ProviderFact]
    public async Task With_all_folders_everyday_mail_comes_from_the_incoming_folders_only()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        foreach (string folder in new[] { "Newsletter", "Gesendet", "Spam" })
        {
            await provider.CreateFolderAsync(folder);
        }

        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Inbox mail", "x"));
        await provider.AppendAsync("Newsletter", RawMail.Build("news@sender.test", "alice@example.test", "Sorted by a server rule", "x"));
        await provider.AppendAsync("Gesendet", RawMail.Build("alice@example.test", "max@sender.test", "Sent by Alice", "x"));
        await provider.AppendAsync("Spam", RawMail.Build("spam@sender.test", "alice@example.test", "You won", "x"));
        MailAccount account = await AddAccountAsync(provider, a => a.SyncAllFolders = true);

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(new[] { "Inbox mail", "Sorted by a server rule" }, (await InboxAsync(_seed.AliceMailbox.Id)).Select(m => m.Subject));
        Assert.Equal(
            new[] { "INBOX", "Newsletter" },
            await QueryAsync(db => db.MailAccountFolderStates.Where(s => s.MailAccountId == account.Id).OrderBy(s => s.RemoteFolder).Select(s => s.RemoteFolder).ToListAsync()));
    }

    [ProviderFact]
    public async Task A_huge_mailbox_is_fetched_in_parts_so_other_accounts_get_their_turn()
    {
        await using TestHost host = await TestHost.CreateAsync(configureServices: s => s.AddMailSync(o => { o.BatchSize = 2; o.MaxMessagesPerRun = 3; }));
        Seed seed = await host.SeedAsync();
        ProviderUser provider = await ProviderUser.CreateAsync();
        for (int i = 1; i <= 5; i++)
        {
            await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", $"Mail {i}", "x"));
        }

        MailAccount account = await AddAccountAsync(provider, host: host);

        SyncReport first = await SyncAsync(account, host);
        Assert.Equal(3, first.Downloaded);
        Assert.True(first.MorePending);
        Assert.Equal("3 new messages; more follow in the next run", first.Message);
        Assert.True((await AccountAsync(account.Id, host)).NextSyncDate <= DateTime.UtcNow);

        SyncReport second = await SyncAsync(account, host);
        Assert.Equal(2, second.Downloaded);
        Assert.False(second.MorePending);
        Assert.Equal(5, await QueryAsync(db => db.MailMessages.CountAsync(m => m.MailboxId == seed.AliceMailbox.Id), host));
    }

    [ProviderFact]
    public async Task A_renumbered_folder_is_compared_again_without_duplicates()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.CreateFolderAsync("Projects");
        byte[] first = RawMail.Build("max@sender.test", "alice@example.test", "Plan", "x", "<plan@sender.test>");
        byte[] second = RawMail.Build("max@sender.test", "alice@example.test", "Budget", "x", "<budget@sender.test>");
        await provider.AppendAsync("Projects", first);
        await provider.AppendAsync("Projects", second);
        MailAccount account = await AddAccountAsync(provider, a => a.SyncFolders = new[] { "Projects" });
        Assert.Equal(2, (await SyncAsync(account)).Downloaded);
        uint oldValidity = await provider.UidValidityAsync("Projects");

        // The provider rebuilds the folder: new UIDVALIDITY, the same two messages and a new one.
        await provider.DeleteFolderAsync("Projects");
        await Task.Delay(1100);
        await provider.CreateFolderAsync("Projects");
        await provider.AppendAsync("Projects", first);
        await provider.AppendAsync("Projects", second);
        await provider.AppendAsync("Projects", RawMail.Build("max@sender.test", "alice@example.test", "Timeline", "x", "<timeline@sender.test>"));
        uint newValidity = await provider.UidValidityAsync("Projects");
        Assert.NotEqual(oldValidity, newValidity);

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal((1, 2), (report.Downloaded, report.Skipped));
        Assert.Contains("renumbered", report.Message);
        Assert.Equal(new[] { "Plan", "Budget", "Timeline" }, (await InboxAsync(_seed.AliceMailbox.Id)).Select(m => m.Subject));
        Assert.Equal(3, (await RecordsAsync(account.Id)).Count);
        MailAccountFolderState folder = await QueryAsync(db => db.MailAccountFolderStates.AsNoTracking().SingleAsync(s => s.MailAccountId == account.Id));
        Assert.Equal(newValidity, (uint)folder.UidValidity);
    }

    [ProviderFact]
    public async Task Read_and_starred_flags_follow_the_side_that_changed()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        uint firstUid = await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "One", "x"));
        uint secondUid = await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Two", "x"));
        MailAccount account = await AddAccountAsync(provider);
        await SyncAsync(account);

        // Read on the phone (directly at the provider): pulled.
        await provider.SetFlagsAsync("INBOX", firstUid, MessageFlags.Seen);
        SyncReport pulled = await SyncAsync(account);
        Assert.Equal(1, pulled.FlagChanges);
        List<MailMessage> inbox = await InboxAsync(_seed.AliceMailbox.Id);
        Assert.True(inbox.Single(m => m.Subject == "One").IsRead);

        // Starred in the web client: pushed.
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().ChangeFlagsAsync(new[] { inbox.Single(m => m.Subject == "Two").Id }, new FlagChange { IsStarred = true });
        }

        SyncReport pushed = await SyncAsync(account);
        Assert.Equal(1, pushed.FlagChanges);
        Assert.True((await provider.FlagsAsync("INBOX", secondUid)).HasFlag(MessageFlags.Flagged));

        // Marked unread again in the web client: pushed as well; then everything is quiet.
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().ChangeFlagsAsync(new[] { inbox.Single(m => m.Subject == "One").Id }, new FlagChange { IsRead = false });
        }

        await SyncAsync(account);
        Assert.False((await provider.FlagsAsync("INBOX", firstUid)).HasFlag(MessageFlags.Seen));
        Assert.Equal(0, (await SyncAsync(account)).FlagChanges);
        Assert.True((await InboxAsync(_seed.AliceMailbox.Id)).Single(m => m.Subject == "Two").IsStarred);
    }

    // ----- robustness -----------------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task A_wrong_password_reports_a_readable_error_and_backs_off()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        MailAccount account = await AddAccountAsync(provider, password: "not-the-password");
        (string host, _) = TestProvider.Imap;

        SyncReport first = await SyncAsync(account);

        Assert.True(first.Started);
        Assert.False(first.Succeeded);
        Assert.Equal($"{host}: the user name or password was refused.", first.Message);
        MailAccount afterFirst = await AccountAsync(account.Id);
        Assert.Equal((SyncState.Error, 1), (afterFirst.LastSyncState, afterFirst.FailureCount));
        Assert.InRange(afterFirst.NextSyncDate!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));

        await SyncAsync(account);
        MailAccount afterSecond = await AccountAsync(account.Id);
        Assert.Equal(2, afterSecond.FailureCount);
        Assert.InRange(afterSecond.NextSyncDate!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));

        // The same error twice is logged once.
        ActivityLog error = Assert.Single(await SyncLogAsync());
        Assert.Equal(ActivityLevel.Error, error.Level);
        Assert.DoesNotContain("not-the-password", error.Message);

        // Fixed password: back to normal, and the log says so.
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            (await db.MailAccounts.SingleAsync(a => a.Id == account.Id)).ReceivePasswordProtected = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect(provider.Password);
            await db.SaveChangesAsync();
        }

        SyncReport fixedRun = await SyncAsync(account);
        Assert.True(fixedRun.Succeeded, fixedRun.Message);
        Assert.Equal((SyncState.Ok, 0), ((await AccountAsync(account.Id)).LastSyncState, (await AccountAsync(account.Id)).FailureCount));
        Assert.Contains("works again", (await SyncLogAsync()).Last().Message);
    }

    [ProviderFact]
    public async Task A_broken_message_is_given_up_after_three_attempts_and_never_blocks_the_others()
    {
        string brokenUid = "?";
        await using TestHost host = await TestHost.CreateAsync(configureServices: s => s.AddMailSync(o =>
        {
            o.BatchSize = 2;
            o.BeforeImport = (_, _, uid) =>
            {
                if (uid == brokenUid)
                {
                    throw new FormatException("This message cannot be read.");
                }
            };
        }));
        Seed seed = await host.SeedAsync();
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Before", "x"));
        brokenUid = (await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Broken", "x"))).ToString();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "After", "x"));
        MailAccount account = await AddAccountAsync(provider, a => a.Retention = ServerRetention.DeleteAfterDownload, host);

        SyncReport first = await SyncAsync(account, host);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal((2, 1, 2), (first.Downloaded, first.Failed, first.DeletedAtProvider));
        Assert.Equal(1, await provider.CountAsync("INBOX"));
        Assert.Equal(1, (await QueryAsync(db => db.MailAccountFolderStates.AsNoTracking().SingleAsync(s => s.MailAccountId == account.Id), host)).LastUid);

        SyncReport second = await SyncAsync(account, host);
        Assert.Equal((0, 1), (second.Downloaded, second.Failed));

        SyncReport third = await SyncAsync(account, host);
        Assert.Equal(1, third.Failed);
        Assert.Contains("1 given up after 3 failed attempts", third.Message);
        RemoteMessageState givenUp = Assert.Single(await RecordsAsync(account.Id, host), r => r.RemoteUid == brokenUid);
        Assert.Null(givenUp.LocalMessageId);

        // The position now moves past the given-up message (UID 3 is already gone at the provider).
        Assert.Equal(2, (await QueryAsync(db => db.MailAccountFolderStates.AsNoTracking().SingleAsync(s => s.MailAccountId == account.Id), host)).LastUid);

        // Never retried, never deleted at the provider, logged once.
        SyncReport fourth = await SyncAsync(account, host);
        Assert.Equal((0, 0), (fourth.Downloaded, fourth.Failed));
        Assert.Equal(1, await provider.CountAsync("INBOX"));
        ActivityLog warning = Assert.Single(await SyncLogAsync(host), l => l.Level == ActivityLevel.Warning);
        Assert.Contains($"UID {brokenUid}", warning.Message);
        Assert.Equal(new[] { "Before", "After" }, await QueryAsync(db => db.MailMessages.Where(m => m.MailboxId == seed.AliceMailbox.Id).OrderBy(m => m.Id).Select(m => m.Subject).ToListAsync(), host));
    }

    // ----- backup and migration -------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task A_backup_copies_every_folder_into_the_backup_mailbox_and_never_deletes()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.CreateFolderAsync("Archiv/2025");
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Inbox one", "x"), MessageFlags.Seen);
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "bob@example.test", "Inbox two", "x"));
        await provider.AppendAsync("Archiv/2025", RawMail.Build("max@sender.test", "alice@example.test", "Old invoice", "x"), MessageFlags.Seen | MessageFlags.Flagged);
        Mailbox backup = await CreateMailboxAsync("Backups");
        MailAccount account = await AddAccountAsync(provider, a =>
        {
            a.Name = "Strato";
            a.Role = MailAccountRole.Backup;
            a.Retention = ServerRetention.DeleteAfterDownload;
            a.TargetMailboxId = backup.Id;
        });

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal((3, 0), (report.Downloaded, report.DeletedAtProvider));
        Assert.Contains("never delete at the provider", report.Message);
        Assert.Equal(2, await provider.CountAsync("INBOX"));
        Assert.Equal(new[] { "Inbox one", "Inbox two" }, (await FolderAsync(backup.Id, "Backup/Strato/INBOX")).Select(m => m.Subject));
        MailMessage archived = Assert.Single(await FolderAsync(backup.Id, "Backup/Strato/Archiv/2025"));
        Assert.True(archived.IsRead && archived.IsStarred);

        // Nothing was routed to the recipients.
        Assert.Empty(await InboxAsync(_seed.AliceMailbox.Id));
        Assert.Empty(await InboxAsync(_seed.BobMailbox.Id));
        Assert.Equal(0, (await SyncAsync(account)).Downloaded);
    }

    [ProviderFact]
    public async Task A_backup_survives_a_renumbered_folder_without_duplicates()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "One", "x"));
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Two", "x"));
        Mailbox backup = await CreateMailboxAsync("Backups");
        MailAccount account = await AddAccountAsync(provider, a => { a.Name = "Strato"; a.Role = MailAccountRole.Backup; a.TargetMailboxId = backup.Id; });
        Assert.Equal(2, (await SyncAsync(account)).Downloaded);

        // As if the provider had rebuilt the folder: the stored UIDVALIDITY no longer matches.
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailAccountFolderStates
                .Where(s => s.MailAccountId == account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UidValidity, x => x.UidValidity - 1));
        }

        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Three", "x"));
        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal((1, 2), (report.Downloaded, report.Skipped));
        Assert.Equal(new[] { "One", "Two", "Three" }, (await FolderAsync(backup.Id, "Backup/Strato/INBOX")).Select(m => m.Subject));
        Assert.Equal(3, (await RecordsAsync(account.Id)).Count(r => r.LocalMessageId is not null));
    }

    [ProviderFact]
    public async Task A_migration_mirrors_the_folder_tree_and_maps_the_special_folders()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        foreach (string folder in new[] { "Gesendet", "Entwürfe", "Papierkorb", "Spam", "Projects/2026", "INBOX/Newsletter" })
        {
            await provider.CreateFolderAsync(folder);
        }

        var oldDate = new DateTimeOffset(2025, 3, 1, 9, 30, 0, TimeSpan.Zero);
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Hello", "x"), MessageFlags.Seen);
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Unread", "x"));
        await provider.AppendAsync("Gesendet", RawMail.Build("alice@example.test", "max@sender.test", "My answer", "x"), MessageFlags.Seen | MessageFlags.Answered);
        await provider.AppendAsync("Entwürfe", RawMail.Build("alice@example.test", "max@sender.test", "Unfinished", "x"), MessageFlags.Draft);
        await provider.AppendAsync("Papierkorb", RawMail.Build("max@sender.test", "alice@example.test", "Deleted", "x"));
        await provider.AppendAsync("Spam", RawMail.Build("spam@sender.test", "alice@example.test", "You won", "x"));
        await provider.AppendAsync("Projects/2026", RawMail.Build("max@sender.test", "alice@example.test", "Plan", "x"), MessageFlags.Flagged, oldDate);
        await provider.AppendAsync("INBOX/Newsletter", RawMail.Build("news@sender.test", "alice@example.test", "News", "x"));
        Mailbox target = await CreateMailboxAsync("Old account");
        MailAccount account = await AddAccountAsync(provider, a => { a.Role = MailAccountRole.Migration; a.TargetMailboxId = target.Id; });

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(8, report.Downloaded);
        List<MailMessage> inbox = await FolderAsync(target.Id, FolderKind.Inbox);
        Assert.Equal(new[] { "Hello", "Unread" }, inbox.Select(m => m.Subject));
        Assert.Equal(new[] { true, false }, inbox.Select(m => m.IsRead));
        MailMessage sent = Assert.Single(await FolderAsync(target.Id, FolderKind.Sent));
        Assert.True(sent.IsRead && sent.IsAnswered);
        Assert.True(Assert.Single(await FolderAsync(target.Id, FolderKind.Drafts)).IsDraft);
        Assert.Equal("Deleted", Assert.Single(await FolderAsync(target.Id, FolderKind.Trash)).Subject);
        Assert.Equal("You won", Assert.Single(await FolderAsync(target.Id, FolderKind.Junk)).Subject);
        MailMessage plan = Assert.Single(await FolderAsync(target.Id, "Projects/2026"));
        Assert.True(plan.IsStarred);
        Assert.Equal(oldDate.UtcDateTime, plan.ReceivedDate);
        Assert.Equal("News", Assert.Single(await FolderAsync(target.Id, "INBOX/Newsletter")).Subject);

        // A migration never routes: the recipients did not get anything.
        Assert.Empty(await InboxAsync(_seed.AliceMailbox.Id));
        Assert.Empty(await InboxAsync(_seed.UnassignedMailboxId));
    }

    [ProviderFact]
    public async Task A_backup_without_a_target_mailbox_fails_with_a_clear_message()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        MailAccount account = await AddAccountAsync(provider, a => a.Role = MailAccountRole.Backup);

        SyncReport report = await SyncAsync(account);

        Assert.False(report.Succeeded);
        Assert.Equal("The backup needs a target mailbox; choose one in the account settings.", report.Message);
    }

    // ----- live access ----------------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task Live_access_lists_metadata_only_and_fetches_the_body_on_demand()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse("max@sender.test"));
        mail.To.Add(MailboxAddress.Parse("alice@example.test"));
        mail.Subject = "Quarterly report";
        mail.MessageId = "report-q3@sender.test";
        var body = new BodyBuilder { TextBody = "The secret body text." };
        body.Attachments.Add("report.pdf", new byte[4096], new ContentType("application", "pdf"));
        mail.Body = body.ToMessageBody();
        uint uid = await provider.AppendAsync("INBOX", mail, MessageFlags.Seen);
        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Second", "x"));
        MailAccount account = await AddAccountAsync(provider, a => a.Retention = ServerRetention.LiveAccess);

        SyncReport report = await SyncAsync(account);

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(2, report.Downloaded);
        MailMessage listed = (await InboxAsync(_seed.AliceMailbox.Id)).Single(m => m.Subject == "Quarterly report");
        Assert.Equal(MessageStorage.Remote, listed.Storage);
        Assert.Equal("max@sender.test", listed.FromAddress);
        Assert.Equal("<report-q3@sender.test>", listed.MessageIdHeader);
        Assert.True(listed.IsRead);
        Assert.True(listed.HasAttachments);
        Assert.True(listed.SizeBytes > 4096);
        Assert.Null(await QueryAsync(db => db.MailMessageContents.Where(c => c.MessageId == listed.Id).Select(c => c.Raw).SingleAsync()));

        using (IServiceScope scope = _host.ScopeAs(_seed.Alice))
        {
            byte[]? raw = await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(listed.Id);
            Assert.NotNull(raw);
            Assert.Contains("The secret body text.", System.Text.Encoding.UTF8.GetString(raw));
            Assert.Equal(listed.SizeBytes, raw.LongLength);
        }

        // Still nothing stored locally.
        Assert.Null(await QueryAsync(db => db.MailMessageContents.Where(c => c.MessageId == listed.Id).Select(c => c.Raw).SingleAsync()));

        // Deleted at the provider by another client: the stand-in goes away with the next run.
        await provider.ExpungeAsync("INBOX", uid);
        SyncReport after = await SyncAsync(account);
        Assert.Contains("1 removed (gone at the provider)", after.Message);
        Assert.Equal("Second", Assert.Single(await InboxAsync(_seed.AliceMailbox.Id)).Subject);
    }

    [ProviderFact]
    public async Task Live_access_never_fetches_from_a_renumbered_folder_and_lists_it_afresh()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        await provider.CreateFolderAsync("Projects");
        byte[] plan = RawMail.Build("max@sender.test", "alice@example.test", "Plan", "Plan body", "<plan@sender.test>");
        await provider.AppendAsync("Projects", plan);
        MailAccount account = await AddAccountAsync(provider, a => { a.Retention = ServerRetention.LiveAccess; a.SyncFolders = new[] { "Projects" }; });
        await SyncAsync(account);
        MailMessage stub = Assert.Single(await InboxAsync(_seed.AliceMailbox.Id));

        // The provider rebuilds the folder with another message first: UID 1 means another message now.
        await provider.DeleteFolderAsync("Projects");
        await Task.Delay(1100);
        await provider.CreateFolderAsync("Projects");
        await provider.AppendAsync("Projects", RawMail.Build("max@sender.test", "alice@example.test", "Other", "Other body", "<other@sender.test>"));
        await provider.AppendAsync("Projects", plan);

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(stub.Id));
        }

        SyncReport report = await SyncAsync(account);

        Assert.Contains("renumbered", report.Message);
        List<MailMessage> listed = await InboxAsync(_seed.AliceMailbox.Id);
        Assert.Equal(new[] { "Other", "Plan" }, listed.Select(m => m.Subject));
        using (IServiceScope scope = _host.Scope())
        {
            byte[]? raw = await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(listed.Single(m => m.Subject == "Plan").Id);
            Assert.Contains("Plan body", System.Text.Encoding.UTF8.GetString(raw!));
        }
    }

    // ----- POP3 -----------------------------------------------------------------------------------------------------

    [ProviderFact]
    public async Task Pop3_fetches_by_uidl_and_deletes_after_download_when_asked()
    {
        ProviderUser provider = await ProviderUser.CreateAsync();
        for (int i = 1; i <= 3; i++)
        {
            await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", $"Pop {i}", "x"));
        }

        (string popHost, int popPort) = TestProvider.Pop3;
        MailAccount account = await AddAccountAsync(provider, a =>
        {
            a.ReceiveProtocol = ReceiveProtocol.Pop3;
            a.ReceiveHost = popHost;
            a.ReceivePort = popPort;
            a.Retention = ServerRetention.LiveAccess;
        });

        SyncReport first = await SyncAsync(account);

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(3, first.Downloaded);
        Assert.Contains("live access needs IMAP", first.Message);
        List<MailMessage> inbox = await InboxAsync(_seed.AliceMailbox.Id);
        Assert.Equal(new[] { "Pop 1", "Pop 2", "Pop 3" }, inbox.Select(m => m.Subject));
        Assert.All(inbox, m => Assert.Equal(MessageStorage.Local, m.Storage));
        Assert.All(await RecordsAsync(account.Id), r => Assert.Equal("INBOX", r.RemoteFolder));

        // Known by UIDL: nothing twice, and the server keeps the messages.
        Assert.Equal(0, (await SyncAsync(account)).Downloaded);
        Assert.Equal(3, await provider.CountAsync("INBOX"));

        // Switched to "delete after download": the stored messages are removed at the provider.
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            (await db.MailAccounts.SingleAsync(a => a.Id == account.Id)).Retention = ServerRetention.DeleteAfterDownload;
            await db.SaveChangesAsync();
        }

        await provider.AppendAsync("INBOX", RawMail.Build("max@sender.test", "alice@example.test", "Pop 4", "x"));
        SyncReport third = await SyncAsync(account);
        Assert.Equal((1, 4), (third.Downloaded, third.DeletedAtProvider));
        Assert.Equal(0, await provider.CountAsync("INBOX"));
        Assert.Empty(await RecordsAsync(account.Id));
        Assert.Equal(4, (await InboxAsync(_seed.AliceMailbox.Id)).Count);
    }
}

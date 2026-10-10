using System.Text.Json;
using MatMail.Api;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Pages.Admin.Mailboxes;
using MatMail.Pages.Admin.Unassigned;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MatMail.Tests;

/// <summary>Helpers for the tests of the storage limit: a mailbox that holds a little and has exactly that much room.</summary>
internal static class QuotaTestSupport
{
    /// <summary>Puts a message of about the given size into the inbox of a mailbox and returns what the mailbox holds now.</summary>
    public static async Task<long> StoreAsync(TestHost host, long mailboxId, int bodyBytes, string subject = "Filler")
    {
        await ImapTestData.AddAsync(host, mailboxId, FolderKind.Inbox, RawMail.Build("max@sender.test", "alice@example.test", subject, new string('x', bodyBytes)));
        return await UsedAsync(host, mailboxId);
    }

    public static async Task<long> UsedAsync(TestHost host, long mailboxId)
    {
        using IServiceScope scope = host.Scope();
        return (await scope.ServiceProvider.GetRequiredService<MailboxUsageService>().GetAsync(mailboxId)).LocalBytes;
    }

    public static async Task SetLimitAsync(TestHost host, long mailboxId, long? bytes)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Mailbox mailbox = await db.Mailboxes.IgnoreQueryFilters().FirstAsync(m => m.Id == mailboxId);
        mailbox.QuotaBytes = bytes;
        await db.SaveChangesAsync();
    }

    /// <summary>Gives the mailbox a message and a limit that this message just reaches: the mailbox is full. Returns what it holds.</summary>
    public static async Task<long> FillUpAsync(TestHost host, long mailboxId)
    {
        long used = await StoreAsync(host, mailboxId, 2000);
        await SetLimitAsync(host, mailboxId, used);
        return used;
    }

    public static async Task<bool> IsFullAsync(TestHost host, long mailboxId)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailboxQuotaService>().IsFullAsync(mailboxId);
    }

    /// <summary>The messages of a mailbox in the folders of a kind, oldest first.</summary>
    public static async Task<List<MailMessage>> MessagesAsync(TestHost host, long mailboxId, FolderKind kind = FolderKind.Inbox)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == kind).OrderBy(m => m.Uid).ToListAsync();
    }
}

/// <summary>
/// The storage limit of a mailbox: when it is full, what it refuses (mail from outside, sending to it, copying into it) and what
/// still works (the owner's own mail, deleting, moving inside the mailbox).
/// </summary>
public class MailboxQuotaTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        AppInfo.DataDir = Path.Combine(Path.GetTempPath(), "matmail-test-data-" + Guid.NewGuid().ToString("N"));   // sending from the web client stages files there ("/data" is not writable in CI)
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<DeliveryResult> DeliverToAsync(string recipient, string subject, MessageStorage storage = MessageStorage.Local)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", recipient, subject, "Hello"),
            new DeliverySource { EnvelopeRecipients = new[] { recipient }, Storage = storage });
    }

    // ----- when is a mailbox full -------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_mailbox_without_a_limit_is_never_full()
    {
        await QuotaTestSupport.StoreAsync(_host, _seed.AliceMailbox.Id, 50_000);

        using IServiceScope scope = _host.Scope();
        var quota = scope.ServiceProvider.GetRequiredService<MailboxQuotaService>();
        Assert.False(await quota.IsFullAsync(_seed.AliceMailbox.Id));
        Assert.False(await quota.IsFullAsync(_seed.AliceMailbox));
        Assert.Empty(await quota.FullAmongAsync(new[] { _seed.AliceMailbox, _seed.BobMailbox }));
    }

    [DbFact]
    public async Task A_mailbox_is_full_when_what_it_holds_has_reached_its_limit()
    {
        long used = await QuotaTestSupport.StoreAsync(_host, _seed.AliceMailbox.Id, 3000);

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, used + 1);
        Assert.False(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, used);
        Assert.True(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, used - 1);
        Assert.True(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));

        // The limit belongs to the mailbox: another one with room is not affected.
        Assert.False(await QuotaTestSupport.IsFullAsync(_host, _seed.BobMailbox.Id));

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Mailbox alice = await db.Mailboxes.AsNoTracking().FirstAsync(m => m.Id == _seed.AliceMailbox.Id);
        var quota = scope.ServiceProvider.GetRequiredService<MailboxQuotaService>();
        Assert.True(await quota.IsFullAsync(alice));
        Assert.Equal(new[] { alice.Id }, (await quota.FullAmongAsync(new[] { alice, alice, _seed.BobMailbox })).Select(m => m.Id));
    }

    [DbFact]
    public async Task What_stays_at_the_provider_takes_no_room_here()
    {
        // A stand-in of live access is only a header here; the message itself is at the provider.
        using (IServiceScope scope = _host.Scope())
        {
            MailFolder inbox = (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox))!;
            await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(
                inbox.Id, new NewMessage(RawMail.Build("max@sender.test", "alice@example.test", "At the provider", new string('x', 5000))) { Storage = MessageStorage.Remote });
        }

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, 1000);

        Assert.False(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));
    }

    [Fact]
    public void The_exception_names_the_mailboxes_that_are_full()
    {
        var one = new MailboxFullException(new[] { new Mailbox { Id = 1, Name = "Alice" } });
        Assert.Equal("The mailbox \"Alice\" is full: it takes no new mail until something is deleted.", one.Message);

        var two = new MailboxFullException(new[] { new Mailbox { Id = 1, Name = "Alice" }, new Mailbox { Id = 2, Name = "Info" } });
        Assert.Equal("The mailboxes \"Alice\", \"Info\" are full: they take no new mail until something is deleted.", two.Message);
        Assert.Equal(new long[] { 1, 2 }, two.MailboxIds);
    }

    // ----- delivery ---------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Mail_for_a_full_mailbox_is_refused_and_nobody_else_gets_it_either()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);

        using IServiceScope scope = _host.Scope();
        var refused = await Assert.ThrowsAsync<MailboxFullException>(() => scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "alice@example.test, bob@example.test", "For two", "x"),
            new DeliverySource { EnvelopeRecipients = new[] { "alice@example.test", "bob@example.test" } }));

        Assert.Equal(new[] { _seed.AliceMailbox.Id }, refused.MailboxIds);
        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));      // all or nothing: a second try must not deliver twice
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));   // only what was there
    }

    [DbFact]
    public async Task Mail_for_a_mailbox_with_room_is_delivered_as_before()
    {
        await QuotaTestSupport.StoreAsync(_host, _seed.AliceMailbox.Id, 1000);
        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, 10_000_000);

        Assert.Equal(1, (await DeliverToAsync("alice@example.test", "Plenty of room")).Delivered);
        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id)).Count);
    }

    [DbFact]
    public async Task Mail_for_the_unassigned_mailbox_follows_the_same_rule()
    {
        // The catch-all of unknown addresses can be limited too (by an administrator in the database): it is a mailbox like any other.
        await QuotaTestSupport.FillUpAsync(_host, _seed.UnassignedMailboxId);

        await Assert.ThrowsAsync<MailboxFullException>(() => DeliverToAsync("nobody@example.test", "Lost"));
    }

    [DbFact]
    public async Task Mail_goes_through_again_when_the_limit_is_raised_or_room_was_made()
    {
        long used = await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);
        MailMessage filler = Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));
        await Assert.ThrowsAsync<MailboxFullException>(() => DeliverToAsync("alice@example.test", "One"));

        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, used * 3);
        Assert.Equal(1, (await DeliverToAsync("alice@example.test", "Two")).Delivered);

        // The limit is reached again; deleting for good makes room.
        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, await QuotaTestSupport.UsedAsync(_host, _seed.AliceMailbox.Id));
        await Assert.ThrowsAsync<MailboxFullException>(() => DeliverToAsync("alice@example.test", "Three"));
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().DeleteAsync(new[] { filler.Id }, permanent: true);
        }

        Assert.Equal(1, (await DeliverToAsync("alice@example.test", "Four")).Delivered);
    }

    [DbFact]
    public async Task Messages_in_the_trash_still_take_room_until_it_is_emptied()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);
        MailMessage filler = Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));

        using (IServiceScope scope = _host.Scope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MailStore>().DeleteAsync(new[] { filler.Id }));   // into the trash
        }

        Assert.True(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));
        await Assert.ThrowsAsync<MailboxFullException>(() => DeliverToAsync("alice@example.test", "Still no room"));

        using (IServiceScope scope = _host.Scope())
        {
            MailFolder trash = (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Trash))!;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MailStore>().EmptyFolderAsync(trash.Id));
        }

        Assert.False(await QuotaTestSupport.IsFullAsync(_host, _seed.AliceMailbox.Id));
        Assert.Equal(1, (await DeliverToAsync("alice@example.test", "Room again")).Delivered);
    }

    [DbFact]
    public async Task A_stand_in_of_live_access_is_never_refused()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);

        DeliveryResult result = await DeliverToAsync("alice@example.test", "Stays at the provider", MessageStorage.Remote);

        Assert.Equal(1, result.Delivered);
        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id)).Count);
    }

    // ----- sending ----------------------------------------------------------------------------------------------------

    private SubmissionRequest Request(string[] recipients, bool saveToSent = false)
        => new()
        {
            Raw = RawMail.Build("Alice <alice@example.test>", string.Join(", ", recipients), "From Alice", "Hello"),
            EnvelopeFrom = "alice@example.test",
            Recipients = recipients,
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
            SaveToSent = saveToSent,
        };

    [DbFact]
    public async Task Sending_to_a_full_mailbox_sends_nothing_at_all_and_says_why()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.BobMailbox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(
            Request(new[] { "bob@example.test", "info@example.test", "friend@outside.test" }, saveToSent: true));

        Assert.False(result.Accepted);
        Assert.True(result.Temporary);
        Assert.Equal(MailSubmission.RecipientMailboxFull, result.Error);
        Assert.Equal(new[] { "bob@example.test" }, result.Rejected);
        Assert.Equal((0, 0), (result.LocalCopies, result.Queued));

        // Nobody got a copy (the others would get it a second time when the sender tries again), nothing was queued or kept in "Sent".
        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id));
        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id, FolderKind.Sent));
        using IServiceScope check = _host.Scope();
        Assert.Equal(0, await check.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.CountAsync());
    }

    [DbFact]
    public async Task The_web_client_is_told_whose_mailbox_is_full()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.BobMailbox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        MailUser alice = (await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync("alice", ImapTestServer.Password, "127.0.0.1"))!;
        ComposeResult result = await scope.ServiceProvider.GetRequiredService<ComposeService>().SendAsync(alice, new ComposeModel
        {
            From = "alice@example.test", To = { "Bob <bob@example.test>" }, Cc = { "info@example.test" }, Subject = "Lunch?", Html = "<p>At noon?</p>",
        });

        Assert.False(result.Ok);
        Assert.Contains("bob@example.test", result.Error);
        Assert.DoesNotContain("info@example.test", result.Error);   // only the mailbox that is full is named
        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id));
        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id, FolderKind.Sent));
    }

    [DbFact]
    public async Task The_owner_of_a_full_mailbox_can_still_send_and_keeps_the_copy_in_sent()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(Request(new[] { "bob@example.test" }, saveToSent: true));

        Assert.True(result.Accepted);
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id, FolderKind.Sent));
    }

    [DbFact]
    public async Task A_full_mailbox_of_another_tenant_refuses_mail_from_this_one_too()
    {
        // Mail between tenants is local mail: the fullness of the other mailbox must be seen although it is not "ours".
        Mailbox carolBox;
        using (IServiceScope setup = _host.Scope())
        {
            (Tenant? other, string? tenantError) = await setup.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Other", null);
            Assert.Null(tenantError);
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.Domains.Add(new Domain { TenantId = other!.Id, Name = "other.test" });
            await db.SaveChangesAsync();
            long role = await db.Roles.Where(r => r.TenantId == other.Id && r.Name == TenantService.UserRoleName).Select(r => r.Id).FirstAsync();
            (User? carol, string? userError) = await setup.ServiceProvider.GetRequiredService<UserService>().CreateAsync(
                new UserInput { LoginName = "carol", DisplayName = "Carol", Password = "Test-Passw0rd!", RoleIds = new[] { role }, CreateMailbox = true, PrimaryAddress = "carol@other.test" }, other.Id);
            Assert.Null(userError);
            carolBox = await db.Mailboxes.IgnoreQueryFilters().FirstAsync(m => m.OwnerUserId == carol!.Id);
        }

        await QuotaTestSupport.FillUpAsync(_host, carolBox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(Request(new[] { "carol@other.test" }));

        Assert.False(result.Accepted);
        Assert.True(result.Temporary);
        Assert.Equal(new[] { "carol@other.test" }, result.Rejected);
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, carolBox.Id));
    }
}

/// <summary>The web client's own ways into a mailbox (moving messages), the info dialog and the pages of the administrator.</summary>
public class MailboxQuotaWebTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static T Prepare<T>(T page) where T : PageModel
    {
        var http = new DefaultHttpContext();
        page.PageContext = new PageContext { HttpContext = http };
        page.TempData = new TempDataDictionary(http, new NoTempData());
        return page;
    }

    private static EditModel EditOf(IServiceScope scope, long id)
    {
        IServiceProvider services = scope.ServiceProvider;
        EditModel page = new(
            services.GetRequiredService<MatMailDbContext>(), services.GetRequiredService<MailboxService>(), services.GetRequiredService<MailboxUsageService>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>()) { Id = id };
        return Prepare(page);
    }

    private static AssignModel AssignOf(IServiceScope scope, long messageId)
    {
        IServiceProvider services = scope.ServiceProvider;
        AssignModel page = new(
            services.GetRequiredService<MatMailDbContext>(), services.GetRequiredService<MailStore>(), services.GetRequiredService<FolderService>(),
            services.GetRequiredService<MailboxService>(), services.GetRequiredService<MailboxQuotaService>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>()) { Id = messageId };
        return Prepare(page);
    }

    private const string MailboxFullText = "This mailbox is full: it takes no new mail until something is deleted.";

    /// <summary>The text in the language of the machine that runs the test (the server answers in the language of the request).</summary>
    private static string Text(IServiceScope scope, string key) => scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()[key].Value;

    /// <summary>What the client would receive: the status and the body of a result.</summary>
    private static async Task<(int Status, string Body)> RunAsync(IResult result, IServiceScope scope)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    private static Task<IResult> MoveAsync(IServiceScope scope, long messageId, long folderId)
        => MailApi.MoveMessages(
            new MoveRequest(new[] { messageId }, folderId), scope.ServiceProvider.GetRequiredService<MailAccessService>(), scope.ServiceProvider.GetRequiredService<MailStore>(),
            scope.ServiceProvider.GetRequiredService<MailboxQuotaService>(), scope.ServiceProvider.GetRequiredService<MatMailDbContext>(), CancellationToken.None);

    private Task<IResult> InfoAsync(IServiceScope scope, long mailboxId)
        => MailApi.MailboxInfo(
            mailboxId, scope.ServiceProvider.GetRequiredService<MailAccessService>(), scope.ServiceProvider.GetRequiredService<MailboxUsageService>(),
            scope.ServiceProvider.GetRequiredService<MatMailDbContext>(), scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>(), CancellationToken.None);

    // ----- moving messages -------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Messages_cannot_be_moved_into_a_full_mailbox_but_inside_a_full_mailbox_they_can()
    {
        await ImapTestData.GrantAsync(_host, _seed.Info, _seed.Alice, MailboxAccess.Edit);
        MailFolder infoInbox = await ImapTestData.FolderAsync(_host, _seed.Info.Id, FolderKind.Inbox);
        MailFolder archive = await ImapTestData.FolderAsync(_host, _seed.AliceMailbox.Id, FolderKind.Archive);
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);
        await QuotaTestSupport.FillUpAsync(_host, _seed.Info.Id);
        MailMessage message = Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));

        // Into the full shared mailbox: refused, and the message stays where it is.
        using (IServiceScope scope = _host.ScopeAs(_seed.Alice))
        {
            (int status, string body) = await RunAsync(await MoveAsync(scope, message.Id, infoInbox.Id), scope);
            Assert.Equal(400, status);
            Assert.Equal(Text(scope, MailboxFullText), JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        }

        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));

        // Inside her own (also full) mailbox nothing is added, so it works.
        using (IServiceScope scope = _host.ScopeAs(_seed.Alice))
        {
            Assert.IsType<Ok<ChangeResult>>(await MoveAsync(scope, message.Id, archive.Id));
        }

        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id, FolderKind.Archive));

        // With room in the target it works across mailboxes too.
        await QuotaTestSupport.SetLimitAsync(_host, _seed.Info.Id, null);
        using (IServiceScope scope = _host.ScopeAs(_seed.Alice))
        {
            Assert.IsType<Ok<ChangeResult>>(await MoveAsync(scope, message.Id, infoInbox.Id));
        }

        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id)).Count);
    }

    // ----- the info dialog -------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_info_of_a_mailbox_tells_the_limit_what_is_used_and_where()
    {
        await QuotaTestSupport.StoreAsync(_host, _seed.AliceMailbox.Id, 3000, "Large");
        await QuotaTestSupport.StoreAsync(_host, _seed.AliceMailbox.Id, 200, "Small");
        await ImapTestData.AddAsync(_host, _seed.AliceMailbox.Id, FolderKind.Sent, RawMail.Build("alice@example.test", "bob@example.test", "Sent one", new string('y', 500)));
        await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, 10_000_000);
        long used = await QuotaTestSupport.UsedAsync(_host, _seed.AliceMailbox.Id);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        MailboxInfoDto info = Assert.IsType<Ok<MailboxInfoDto>>(await InfoAsync(scope, _seed.AliceMailbox.Id)).Value!;

        Assert.Equal(("Personal", true, "Manage", "Alice"), (info.Type, info.IsOwn, info.Access, info.Owner));
        Assert.Equal(new[] { "alice@example.test" }, info.Addresses);
        Assert.Equal((3L, used, 0L, (long?)10_000_000), (info.Messages, info.UsedBytes, info.RemoteBytes, info.QuotaBytes));
        Assert.Equal(new[] { "INBOX", "Sent" }, info.Folders.Select(f => f.Path));   // the larger first, empty folders left out
        Assert.Equal(used, info.Folders.Sum(f => f.Bytes));
        Assert.Equal((2L, 1L), (info.Folders[0].Messages, info.Folders[1].Messages));
    }

    [DbFact]
    public async Task The_info_of_a_shared_mailbox_has_no_owner_and_none_without_access()
    {
        await ImapTestData.GrantAsync(_host, _seed.Info, _seed.Alice, MailboxAccess.Read);

        using (IServiceScope scope = _host.ScopeAs(_seed.Alice))
        {
            MailboxInfoDto info = Assert.IsType<Ok<MailboxInfoDto>>(await InfoAsync(scope, _seed.Info.Id)).Value!;
            Assert.Equal(("Shared", false, "Read", null), (info.Type, info.IsOwn, info.Access, info.Owner));
            Assert.Equal(new[] { "info@example.test" }, info.Addresses);
            Assert.Null(info.QuotaBytes);
            Assert.Empty(info.Folders);
        }

        // Bob may not read Alice's mailbox: he does not learn anything about it, not even that it exists.
        using (IServiceScope scope = _host.ScopeAs(_seed.Bob))
        {
            Assert.IsNotType<Ok<MailboxInfoDto>>(await InfoAsync(scope, _seed.AliceMailbox.Id));
        }
    }

    // ----- the administrator's pages ---------------------------------------------------------------------------------

    [DbFact]
    public async Task The_administrator_sets_the_limit_in_gigabytes_and_it_is_stored_in_bytes()
    {
        long id = _seed.AliceMailbox.Id;
        using IServiceScope scope = _host.ScopeAs(_seed.Alice);

        async Task<long?> SaveAsync(decimal? gigabytes)
        {
            EditModel page = EditOf(scope, id);
            page.Input = new EditModel.InputModel { Name = "Alice", Type = nameof(MailboxType.Personal), OwnerUserId = _seed.Alice.Id, IsActive = true, QuotaGb = gigabytes };
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());
            using IServiceScope check = _host.Scope();
            return await check.ServiceProvider.GetRequiredService<MatMailDbContext>().Mailboxes.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.Id == id).Select(m => m.QuotaBytes).SingleAsync();
        }

        Assert.Equal(2_684_354_560L, await SaveAsync(2.5m));
        Assert.Equal(1024L * 1024 * 1024 * 100_000, await SaveAsync(100_000m));
        Assert.Equal(1024L * 1024, await SaveAsync(0.0001m));   // not less than a megabyte: a limit of a few bytes would be a mailbox that takes nothing
        Assert.Null(await SaveAsync(null));
        Assert.Equal(1024L * 1024 * 1024, await SaveAsync(1m));
        Assert.Null(await SaveAsync(0m));                        // 0 is "no limit", like an empty field
    }

    [DbFact]
    public async Task A_limit_that_makes_no_sense_is_not_saved()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        foreach (decimal wrong in new[] { -1m, 100_000.5m })
        {
            EditModel page = EditOf(scope, _seed.AliceMailbox.Id);
            page.Input = new EditModel.InputModel { Name = "Alice", Type = nameof(MailboxType.Personal), OwnerUserId = _seed.Alice.Id, QuotaGb = wrong };

            Assert.IsType<PageResult>(await page.OnPostAsync());
            Assert.True(page.ModelState["Input.QuotaGb"]!.Errors.Count > 0);
        }

        using IServiceScope check = _host.Scope();
        Assert.Null(await check.ServiceProvider.GetRequiredService<MatMailDbContext>().Mailboxes.AsNoTracking().Where(m => m.Id == _seed.AliceMailbox.Id).Select(m => m.QuotaBytes).SingleAsync());
    }

    [DbFact]
    public async Task The_edit_page_shows_the_limit_and_the_use_and_a_new_mailbox_can_have_a_limit()
    {
        await QuotaTestSupport.StoreAsync(_host, _seed.Info.Id, 4000);
        await QuotaTestSupport.SetLimitAsync(_host, _seed.Info.Id, 2_684_354_560L);

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        EditModel edit = EditOf(scope, _seed.Info.Id);
        Assert.IsType<PageResult>(await edit.OnGetAsync());
        Assert.Equal(2.5m, edit.Input.QuotaGb);
        Assert.Equal(2_684_354_560L, edit.QuotaBytesStored);
        Assert.True(edit.Usage.LocalBytes > 4000);

        EditModel create = EditOf(scope, 0);
        create.Input = new EditModel.InputModel { Name = "Team", Type = nameof(MailboxType.Shared), QuotaGb = 1m };
        Assert.IsType<RedirectToPageResult>(await create.OnPostAsync());
        using IServiceScope check = _host.Scope();
        Mailbox team = await check.ServiceProvider.GetRequiredService<MatMailDbContext>().Mailboxes.AsNoTracking().SingleAsync(m => m.Name == "Team");
        Assert.Equal(1024L * 1024 * 1024, team.QuotaBytes);
    }

    [DbFact]
    public async Task Unassigned_mail_is_not_handed_over_to_a_full_mailbox()
    {
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
                RawMail.Build("max@sender.test", "nobody@example.test", "Lost?", "x"), new DeliverySource { EnvelopeRecipients = new[] { "nobody@example.test" } });
        }

        MailMessage lost = Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.UnassignedMailboxId));
        await QuotaTestSupport.FillUpAsync(_host, _seed.Info.Id);

        using (IServiceScope scope = _host.Scope())
        {
            AssignModel page = AssignOf(scope, lost.Id);
            page.Input = new AssignModel.InputModel { MailboxId = _seed.Info.Id, AddAddresses = false };
            Assert.IsType<PageResult>(await page.OnPostAsync());
            Assert.Equal(Text(scope, MailboxFullText), page.ModelState["Input.MailboxId"]!.Errors.Single().ErrorMessage);
        }

        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.UnassignedMailboxId));
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id));   // only the filler

        await QuotaTestSupport.SetLimitAsync(_host, _seed.Info.Id, null);
        using (IServiceScope scope = _host.Scope())
        {
            AssignModel page = AssignOf(scope, lost.Id);
            page.Input = new AssignModel.InputModel { MailboxId = _seed.Info.Id, AddAddresses = false };
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());
        }

        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.UnassignedMailboxId));
        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id)).Count);
    }
}

/// <summary>The bar of a storage limit: how full it says it is and what colour it gets.</summary>
public class QuotaBarTests
{
    [Theory]
    [InlineData(0, 100, 0, "")]
    [InlineData(74, 100, 74, "")]
    [InlineData(75, 100, 75, "warn")]
    [InlineData(89, 100, 89, "warn")]
    [InlineData(90, 100, 90, "high")]
    [InlineData(999, 1000, 99, "high")]        // 100 % only when the limit is reached
    [InlineData(1000, 1000, 100, "full")]
    [InlineData(2500, 1000, 100, "full")]
    public void The_bar_says_how_full_the_limit_is(long used, long limit, int percent, string state)
    {
        Assert.Equal(percent, Fmt.Percent(used, limit));
        Assert.Equal(state, Fmt.UsageState(used, limit));
    }

    [Fact]
    public void Without_a_limit_there_is_nothing_to_say()
    {
        Assert.Equal(0, Fmt.Percent(5000, 0));
        Assert.Equal(string.Empty, Fmt.UsageState(5000, 0));
    }
}

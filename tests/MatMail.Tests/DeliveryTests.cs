using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

public class DeliveryTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<List<MailMessage>> InboxOfAsync(long mailboxId)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        return await db.MailMessages.AsNoTracking().Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == FolderKind.Inbox).OrderBy(m => m.Uid).ToListAsync();
    }

    [DbFact]
    public async Task Mail_goes_to_the_mailbox_of_the_recipient_address()
    {
        using IServiceScope scope = _host.Scope();
        var delivery = scope.ServiceProvider.GetRequiredService<MailDelivery>();

        DeliveryResult result = await delivery.DeliverAsync(
            RawMail.Build("max@sender.test", "alice@example.test", "Hello", "x"),
            new DeliverySource { EnvelopeRecipients = new[] { "alice@example.test" } });

        Assert.Equal(1, result.Delivered);
        Assert.Single(await InboxOfAsync(_seed.AliceMailbox.Id));
        Assert.Empty(await InboxOfAsync(_seed.BobMailbox.Id));
    }

    [DbFact]
    public async Task One_copy_per_mailbox_even_with_several_matching_recipients()
    {
        using IServiceScope scope = _host.Scope();
        var mailboxes = scope.ServiceProvider.GetRequiredService<MailboxService>();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Mailbox alice = await db.Mailboxes.FirstAsync(m => m.Id == _seed.AliceMailbox.Id);
        Assert.Null(await mailboxes.AddAddressAsync(alice, "alice.m@example.test", isPrimary: false));

        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "alice@example.test, alice.m@example.test, bob@example.test", "Two aliases", "x"),
            new DeliverySource());

        Assert.Equal(2, result.Delivered);
        Assert.Single(await InboxOfAsync(_seed.AliceMailbox.Id));
        Assert.Single(await InboxOfAsync(_seed.BobMailbox.Id));
    }

    [DbFact]
    public async Task Unknown_addresses_of_a_registered_domain_end_up_in_unassigned()
    {
        using IServiceScope scope = _host.Scope();
        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "nobody@example.test", "Lost?", "x"),
            new DeliverySource { EnvelopeRecipients = new[] { "nobody@example.test" } });

        DeliveredCopy copy = Assert.Single(result.Copies);
        Assert.True(copy.WentToUnassigned);
        Assert.Equal(_seed.UnassignedMailboxId, copy.Mailbox.Id);
        MailMessage stored = Assert.Single(await InboxOfAsync(_seed.UnassignedMailboxId));
        Assert.Equal("nobody@example.test", stored.EnvelopeRecipients);
    }

    [DbFact]
    public async Task A_catch_all_address_collects_the_rest_of_the_domain()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Mailbox info = await db.Mailboxes.FirstAsync(m => m.Id == _seed.Info.Id);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<MailboxService>().AddAddressAsync(info, "*@example.test", isPrimary: false));

        await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "anything@example.test", "Catch", "x"),
            new DeliverySource { EnvelopeRecipients = new[] { "anything@example.test" } });

        Assert.Single(await InboxOfAsync(_seed.Info.Id));
        Assert.Empty(await InboxOfAsync(_seed.UnassignedMailboxId));
    }

    [DbFact]
    public async Task A_domain_can_name_a_fallback_mailbox_for_unknown_addresses()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        (await db.Domains.FirstAsync(d => d.Name == TestHost.Domain)).CatchAllMailboxId = _seed.BobMailbox.Id;
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "x@example.test", "Fallback", "x"),
            new DeliverySource { EnvelopeRecipients = new[] { "x@example.test" } });

        Assert.Single(await InboxOfAsync(_seed.BobMailbox.Id));
    }

    [DbFact]
    public async Task A_catch_all_provider_account_routes_by_the_delivered_to_header()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount { TenantId = _seed.Tenant.Id, Name = "Strato catch-all", Address = "catchall@example.test", IsCatchAll = true };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();

        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "somebody@elsewhere.test", "Via provider", "x", extraHeaders: "Delivered-To: bob@example.test\r\n"),
            new DeliverySource { Account = account, TenantId = account.TenantId, RemoteFolder = "INBOX", RemoteUid = "17" });

        Assert.Equal(1, result.Delivered);
        MailMessage stored = Assert.Single(await InboxOfAsync(_seed.BobMailbox.Id));
        Assert.Equal(account.Id, stored.SourceAccountId);
        Assert.Equal("17", stored.RemoteUid);
    }

    [DbFact]
    public async Task A_plain_provider_account_delivers_for_its_own_address_and_unknown_ones_go_to_unassigned()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount { TenantId = _seed.Tenant.Id, Name = "Stray", Address = "max.mueller@example.test" };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();

        // max.mueller@ exists as a provider account, but no user has that address: the message must not be lost.
        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("friend@sender.test", "max.mueller@example.test", "For Max", "x"),
            new DeliverySource { Account = account, TenantId = account.TenantId });

        Assert.True(Assert.Single(result.Copies).WentToUnassigned);
        Assert.Single(await InboxOfAsync(_seed.UnassignedMailboxId));
    }

    [DbFact]
    public async Task A_provider_account_cannot_deliver_into_another_tenant()
    {
        using IServiceScope scope = _host.Scope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantService>();
        (Tenant? other, _) = await tenants.CreateAsync("Other", null);
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount { TenantId = other!.Id, Name = "Other", Address = "o@other.test" };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();

        // The mail names Alice (tenant Home), but the account belongs to "Other": Alice's mailbox stays untouched.
        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", "alice@example.test", "Sneaky", "x", extraHeaders: "Delivered-To: alice@example.test\r\n"),
            new DeliverySource { Account = account, TenantId = other.Id });

        Assert.Empty(await InboxOfAsync(_seed.AliceMailbox.Id));
        Assert.All(result.Copies, c => Assert.Equal(MailboxType.Unassigned, c.Mailbox.Type));
    }

    [DbFact]
    public async Task The_same_message_is_not_stored_twice_in_one_mailbox()
    {
        using IServiceScope scope = _host.Scope();
        var delivery = scope.ServiceProvider.GetRequiredService<MailDelivery>();
        byte[] raw = RawMail.Build("max@sender.test", "alice@example.test", "Once", "x", messageId: "<same@sender.test>");

        await delivery.DeliverAsync(raw, new DeliverySource { EnvelopeRecipients = new[] { "alice@example.test" } });
        DeliveryResult second = await delivery.DeliverAsync(raw, new DeliverySource { EnvelopeRecipients = new[] { "alice@example.test" } });

        Assert.Equal(0, second.Delivered);
        Assert.Single(await InboxOfAsync(_seed.AliceMailbox.Id));
    }
}

public class SubmissionTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DbFact]
    public async Task Local_recipients_get_the_mail_at_once_external_ones_are_queued_and_a_copy_lands_in_sent()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            TenantId = _seed.Tenant.Id, Name = "Strato", Address = "alice@example.test", SendHost = "smtp.strato.test", ReceiveProtocol = ReceiveProtocol.None,
        });
        await db.SaveChangesAsync();

        byte[] raw = RawMail.Build("Alice <alice@example.test>", "bob@example.test, friend@outside.test", "Hi all", "Hello", cc: "secret-cc@outside.test");
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = raw,
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test", "friend@outside.test", "hidden-bcc@outside.test" },
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
            SaveToSent = true,
        });

        Assert.True(result.Accepted);
        Assert.Equal(1, result.LocalCopies);
        Assert.Equal(1, result.Queued);

        OutboundMessage queued = await db.OutboundMessages.AsNoTracking().SingleAsync();
        Assert.Equal(new[] { "friend@outside.test", "hidden-bcc@outside.test" }.OrderBy(a => a), queued.Recipients.OrderBy(a => a));
        Assert.NotNull(queued.MailAccountId);
        Assert.Equal(OutboundStatus.Pending, queued.Status);

        Assert.Equal(1, await db.MailMessages.CountAsync(m => m.MailboxId == _seed.BobMailbox.Id));
        Assert.Equal(1, await db.MailMessages.CountAsync(m => m.MailboxId == _seed.AliceMailbox.Id && m.Folder!.Kind == FolderKind.Sent && m.IsRead));
    }

    [DbFact]
    public async Task Footers_are_appended_to_every_outgoing_message()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.Signatures.Add(new Signature
        {
            TenantId = _seed.Tenant.Id, Name = "Disclaimer", Kind = SignatureKind.Footer, Scope = SignatureScope.Tenant,
            Html = "<p>Sent by {{DisplayName}} at {{Tenant}}</p>",
        });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = RawMail.Build("Alice <alice@example.test>", "bob@example.test", "Footer test", "Body text"),
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test" },
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
        });

        MailMessage delivered = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.BobMailbox.Id);
        byte[] raw = (await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(delivered.Id))!;
        string text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.Contains("Sent by Alice at Home", text);
        Assert.Contains("Body text", text);
    }

    [DbFact]
    public async Task Bcc_recipients_are_not_visible_to_the_others()
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        byte[] raw = RawMail.Build("Alice <alice@example.test>", "bob@example.test", "Secret", "x", extraHeaders: "Bcc: info@example.test\r\n");

        await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = raw,
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test", "info@example.test" },
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
            SaveToSent = true,
        });

        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        MailMessage atBob = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.BobMailbox.Id);
        Assert.DoesNotContain("Bcc:", System.Text.Encoding.UTF8.GetString((await store.GetRawAsync(atBob.Id))!));
        Assert.Equal(1, await db.MailMessages.CountAsync(m => m.MailboxId == _seed.Info.Id));

        MailMessage sent = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.AliceMailbox.Id && m.Folder!.Kind == FolderKind.Sent);
        Assert.Contains("Bcc:", System.Text.Encoding.UTF8.GetString((await store.GetRawAsync(sent.Id))!));
    }

    [DbFact]
    public async Task Without_an_account_and_without_direct_delivery_external_mail_is_refused()
    {
        await using TestHost host = await TestHost.CreateAsync(c => c.Queue.AllowDirectDelivery = false);
        Seed seed = await host.SeedAsync();
        using IServiceScope scope = host.Scope();

        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = RawMail.Build("alice@example.test", "friend@outside.test", "Nope", "x"),
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "friend@outside.test" },
            TenantId = seed.Tenant.Id,
        });

        Assert.False(result.Accepted);
        Assert.Equal(new[] { "friend@outside.test" }, result.Rejected);
    }
}

public class AccessTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailUser> UserAsync(IServiceScope scope, User user, params string[] permissions)
    {
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();
        var user2 = new MailUser(user.Id, user.TenantId, user.LoginName, user.DisplayName, false, permissions.Length == 0 ? new HashSet<string> { Permissions.MailUse } : permissions.ToHashSet());
        access.Apply(user2);
        await Task.CompletedTask;
        return user2;
    }

    [DbFact]
    public async Task Users_sign_in_with_their_password_and_need_the_mail_permission()
    {
        using IServiceScope scope = _host.Scope();
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();

        Assert.NotNull(await access.AuthenticateAsync("Alice", "Test-Passw0rd!", "127.0.0.1"));
        Assert.Null(await access.AuthenticateAsync("alice", "wrong-password", "127.0.0.1"));
        Assert.Null(await access.AuthenticateAsync("nobody", "Test-Passw0rd!", "127.0.0.1"));
    }

    [DbFact]
    public async Task The_address_of_a_personal_mailbox_signs_in_its_owner_but_a_shared_address_does_not()
    {
        using IServiceScope scope = _host.Scope();
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();

        MailUser? byAddress = await access.AuthenticateAsync(" Alice@Example.test ", "Test-Passw0rd!", "127.0.0.1");
        Assert.NotNull(byAddress);
        Assert.Equal(_seed.Alice.Id, byAddress.UserId);
        Assert.Null(await access.AuthenticateAsync("alice@example.test", "wrong-password", "127.0.0.1"));
        Assert.Null(await access.AuthenticateAsync("info@example.test", "Test-Passw0rd!", "127.0.0.1"));
    }

    [DbFact]
    public async Task A_user_sees_their_own_mailbox_and_what_is_delegated_to_them()
    {
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.MailboxPermissions.Add(new MailboxPermission { TenantId = _seed.Tenant.Id, MailboxId = _seed.Info.Id, UserId = _seed.Alice.Id, Access = MailboxAccess.Send });
            db.MailboxPermissions.Add(new MailboxPermission { TenantId = _seed.Tenant.Id, MailboxId = _seed.BobMailbox.Id, UserId = _seed.Alice.Id, Access = MailboxAccess.Read });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        MailUser alice = await UserAsync(scope, _seed.Alice);
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();

        IReadOnlyList<AccessibleMailbox> mailboxes = await access.GetMailboxesAsync(alice);
        Assert.Equal(3, mailboxes.Count);
        Assert.Equal(MailboxAccess.Manage, mailboxes.Single(m => m.IsOwn).Access);
        Assert.Equal(MailboxAccess.Send, mailboxes.Single(m => m.Mailbox.Id == _seed.Info.Id).Access);
        Assert.Equal(MailboxAccess.Read, mailboxes.Single(m => m.Mailbox.Id == _seed.BobMailbox.Id).Access);

        // Alice may send as info@ (Send right), but not as bob@ (Read only).
        Assert.NotNull(await access.FindSendIdentityAsync(alice, "alice@example.test"));
        Assert.NotNull(await access.FindSendIdentityAsync(alice, "info@example.test"));
        Assert.Null(await access.FindSendIdentityAsync(alice, "bob@example.test"));
    }

    [DbFact]
    public async Task Administrators_do_not_read_other_peoples_mail_but_may_open_unassigned()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice, Permissions.MailUse, Permissions.UsersManage, Permissions.UnassignedManage);
        MailUser admin = await UserAsync(scope, _seed.Alice, Permissions.MailUse, Permissions.UsersManage, Permissions.UnassignedManage);
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();

        IReadOnlyList<AccessibleMailbox> mailboxes = await access.GetMailboxesAsync(admin);
        Assert.DoesNotContain(mailboxes, m => m.Mailbox.Id == _seed.BobMailbox.Id);
        Assert.Contains(mailboxes, m => m.Mailbox.Type == MailboxType.Unassigned);
    }

    [DbFact]
    public async Task The_tenant_filter_hides_other_tenants_and_writes_across_tenants_are_refused()
    {
        long otherTenantId;
        using (IServiceScope setup = _host.Scope())
        {
            (Tenant? other, _) = await setup.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Customer GmbH", null);
            otherTenantId = other!.Id;
        }

        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        // Only the own tenant's rows are visible.
        Assert.All(await db.Mailboxes.ToListAsync(), m => Assert.Equal(_seed.Tenant.Id, m.TenantId));
        Assert.All(await db.Roles.ToListAsync(), r => Assert.Equal(_seed.Tenant.Id, r.TenantId));
        Assert.Empty(await db.Users.Where(u => u.TenantId == otherTenantId).ToListAsync());

        // A write into another tenant is refused.
        db.Domains.Add(new Domain { TenantId = otherTenantId, Name = "evil.test" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}

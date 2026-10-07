using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

public class MessageParserTests
{
    [Fact]
    public void Reads_the_facts_the_store_keeps()
    {
        byte[] raw = RawMail.Build("Max Mueller <max@sender.test>", "alice@example.test, Bob <bob@example.test>", "Re: AW: Offer for you", "Hello Alice,\r\n\r\nthis is the offer.", "<abc@sender.test>");
        ParsedMessage parsed = MessageParser.Parse(raw);

        Assert.Equal("Re: AW: Offer for you", parsed.Subject);
        Assert.Equal("Max Mueller", parsed.FromName);
        Assert.Equal("max@sender.test", parsed.FromAddress);
        Assert.Contains("Bob <bob@example.test>", parsed.ToSummary);
        Assert.Equal("<abc@sender.test>", parsed.MessageId);
        Assert.Equal("<abc@sender.test>", parsed.ThreadKey);
        Assert.StartsWith("Hello Alice, this is the offer.", parsed.Preview);
        Assert.Equal(new[] { "alice@example.test", "bob@example.test" }, parsed.Recipients);
        Assert.False(parsed.HasAttachments);
        Assert.EndsWith("\r\n\r\n", System.Text.Encoding.UTF8.GetString(parsed.HeaderBytes));
    }

    [Fact]
    public void A_reply_joins_the_thread_of_its_first_reference()
    {
        byte[] raw = RawMail.Build("a@x.test", "b@y.test", "Re: Plan", "ok", extraHeaders: "In-Reply-To: <second@x.test>\r\nReferences: <root@x.test> <second@x.test>\r\n");
        Assert.Equal("<root@x.test>", MessageParser.Parse(raw).ThreadKey);
    }

    [Theory]
    [InlineData("Re: Re: Fwd: Plan", "plan")]
    [InlineData("AW: WG: Rechnung", "rechnung")]
    [InlineData("Plain subject", "plain subject")]
    public void Subjects_lose_their_reply_prefixes(string subject, string expected)
        => Assert.Equal(expected, MessageParser.NormalizeSubject(subject));

    [Fact]
    public void Html_mail_gets_a_text_preview()
    {
        string html = "<html><head><style>p{color:red}</style></head><body><p>Hello&nbsp;<b>world</b></p><div>Second line</div></body></html>";
        string text = HtmlText.ToPlainText(html);
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
        Assert.Contains("Second line", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", true)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/8", "10.250.3.4", true)]
    [InlineData("fd00::/8", "fd12::1", true)]
    [InlineData("not a network", "10.0.0.1", false)]
    public void Relay_networks_match_addresses(string network, string address, bool expected)
        => Assert.Equal(expected, RelayPolicy.Matches(network, System.Net.IPAddress.Parse(address)));
}

public class MailStoreTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailFolder> FolderAsync(IServiceScope scope, FolderKind kind, Mailbox? mailbox = null)
        => (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync((mailbox ?? _seed.AliceMailbox).Id, kind))!;

    [DbFact]
    public async Task Every_mailbox_starts_with_the_default_folders()
    {
        using IServiceScope scope = _host.Scope();
        IReadOnlyList<FolderInfo> folders = await scope.ServiceProvider.GetRequiredService<FolderService>().ListAsync(_seed.AliceMailbox.Id);
        Assert.Equal(new[] { "INBOX", "Drafts", "Sent", "Archive", "Junk", "Trash" }, folders.Select(f => f.Path));
    }

    [DbFact]
    public async Task New_messages_get_ascending_uids_and_keep_their_state()
    {
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        MailFolder inbox = await FolderAsync(scope, FolderKind.Inbox);

        MailMessage first = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "One", "1")) { IsStarred = true });
        MailMessage second = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Two", "2")) { IsRead = true });

        Assert.Equal(1, first.Uid);
        Assert.Equal(2, second.Uid);
        Assert.True(first.IsStarred);
        Assert.True(second.IsRead);
        Assert.True(second.ModSeq > first.ModSeq);

        byte[]? raw = await store.GetRawAsync(first.Id);
        Assert.NotNull(raw);
        Assert.Contains("Subject: One", System.Text.Encoding.UTF8.GetString(raw));

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailFolder reloaded = await db.MailFolders.AsNoTracking().FirstAsync(f => f.Id == inbox.Id);
        Assert.Equal(3, reloaded.UidNext);
    }

    [DbFact]
    public async Task Parallel_deliveries_never_share_a_uid()
    {
        MailFolder inbox;
        using (IServiceScope scope = _host.Scope())
        {
            inbox = await FolderAsync(scope, FolderKind.Inbox);
        }

        await Task.WhenAll(Enumerable.Range(0, 25).Select(async i =>
        {
            using IServiceScope scope = _host.Scope();
            await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", $"Mail {i}", "x")));
        }));

        using IServiceScope check = _host.Scope();
        var db = check.ServiceProvider.GetRequiredService<MatMailDbContext>();
        List<long> uids = await db.MailMessages.Where(m => m.FolderId == inbox.Id).Select(m => m.Uid).OrderBy(u => u).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), uids);
    }

    [DbFact]
    public async Task Flags_move_copy_and_delete_work_together()
    {
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailFolder inbox = await FolderAsync(scope, FolderKind.Inbox);
        MailFolder archive = await FolderAsync(scope, FolderKind.Archive);
        MailFolder trash = await FolderAsync(scope, FolderKind.Trash);

        MailMessage message = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Move me", "x")));

        Assert.Equal(1, await store.ChangeFlagsAsync(new[] { message.Id }, new FlagChange { IsRead = true, AddKeywords = new[] { "$Label1" } }));
        db.ChangeTracker.Clear();
        MailMessage flagged = await db.MailMessages.FirstAsync(m => m.Id == message.Id);
        Assert.True(flagged.IsRead);
        Assert.Contains("$Label1", flagged.Keywords);
        Assert.Equal(0, await store.ChangeFlagsAsync(new[] { message.Id }, new FlagChange { IsRead = true }));

        // Move: new UID in the target folder, gone from the source.
        await store.MoveAsync(new[] { message.Id }, archive.Id);
        db.ChangeTracker.Clear();
        MailMessage moved = await db.MailMessages.FirstAsync(m => m.Id == message.Id);
        Assert.Equal(archive.Id, moved.FolderId);
        Assert.Equal(1, moved.Uid);

        // Copy keeps the original and creates a second message.
        IReadOnlyList<MailMessage> copies = await store.CopyAsync(new[] { message.Id }, inbox.Id);
        Assert.Single(copies);
        Assert.True(copies[0].IsRead);

        // Delete goes to the trash first, and removes for good from there.
        Assert.Equal(1, await store.DeleteAsync(new[] { message.Id }));
        db.ChangeTracker.Clear();
        Assert.Equal(trash.Id, (await db.MailMessages.FirstAsync(m => m.Id == message.Id)).FolderId);
        Assert.Equal(1, await store.DeleteAsync(new[] { message.Id }));
        Assert.False(await db.MailMessages.AnyAsync(m => m.Id == message.Id));
    }

    [DbFact]
    public async Task Expunge_removes_only_messages_marked_deleted()
    {
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        MailFolder inbox = await FolderAsync(scope, FolderKind.Inbox);
        MailMessage keep = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Keep", "x")));
        MailMessage drop = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Drop", "x")) { IsDeleted = true });

        IReadOnlyList<long> removed = await store.ExpungeAsync(inbox.Id);
        Assert.Equal(new[] { drop.Uid }, removed);

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.True(await db.MailMessages.AnyAsync(m => m.Id == keep.Id));
        Assert.False(await db.MailMessages.AnyAsync(m => m.Id == drop.Id));
    }

    [DbFact]
    public async Task Counts_report_total_and_unread()
    {
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        MailFolder inbox = await FolderAsync(scope, FolderKind.Inbox);
        await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "1", "x")));
        await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "2", "x")) { IsRead = true });

        var counts = await store.GetCountsAsync(new[] { inbox.Id });
        Assert.Equal((2, 1), counts[inbox.Id]);
    }

    [DbFact]
    public async Task Listeners_hear_about_new_mail()
    {
        using IServiceScope scope = _host.Scope();
        var hub = _host.Services.GetRequiredService<MailEventHub>();
        var events = new List<MailEvent>();
        using IDisposable subscription = hub.Subscribe(events.Add);

        MailFolder inbox = await FolderAsync(scope, FolderKind.Inbox);
        await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(inbox.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Hi", "x")));

        MailEvent evt = Assert.Single(events);
        Assert.Equal(MailEventKind.NewMessage, evt.Kind);
        Assert.Equal(_seed.AliceMailbox.Id, evt.MailboxId);
    }
}

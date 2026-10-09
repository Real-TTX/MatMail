using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The storage a mailbox occupies.</summary>
public class MailboxUsageTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailMessage> AddAsync(Mailbox mailbox, FolderKind kind, string subject, int bodyBytes, MessageStorage storage = MessageStorage.Local)
    {
        using IServiceScope scope = _host.Scope();
        MailFolder folder = (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync(mailbox.Id, kind))!;
        return await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(
            folder.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", subject, new string('x', bodyBytes))) { Storage = storage });
    }

    [DbFact]
    public async Task A_mailbox_adds_up_its_messages_and_keeps_what_stays_at_the_provider_apart()
    {
        MailMessage one = await AddAsync(_seed.AliceMailbox, FolderKind.Inbox, "One", 1000);
        MailMessage two = await AddAsync(_seed.AliceMailbox, FolderKind.Sent, "Two", 5000);
        MailMessage stub = await AddAsync(_seed.AliceMailbox, FolderKind.Inbox, "Stays at the provider", 200, MessageStorage.Remote);
        await AddAsync(_seed.BobMailbox, FolderKind.Inbox, "Bob's", 9000);

        using IServiceScope scope = _host.Scope();
        var usage = scope.ServiceProvider.GetRequiredService<MailboxUsageService>();

        MailboxUsage alice = await usage.GetAsync(_seed.AliceMailbox.Id);
        Assert.Equal(3, alice.Messages);
        Assert.Equal(one.SizeBytes + two.SizeBytes, alice.LocalBytes);
        Assert.Equal(stub.SizeBytes, alice.RemoteBytes);

        Dictionary<long, MailboxUsage> both = await usage.GetAsync(new[] { _seed.AliceMailbox.Id, _seed.BobMailbox.Id, _seed.Info.Id });
        Assert.Equal(alice, both[_seed.AliceMailbox.Id]);
        Assert.True(both[_seed.BobMailbox.Id].LocalBytes > 9000);
        Assert.Equal(MailboxUsage.Empty, both[_seed.Info.Id]);   // an empty mailbox is there, with zeros
    }

    [DbFact]
    public async Task The_folders_of_a_mailbox_show_what_each_holds_in_tree_order()
    {
        MailMessage inbox = await AddAsync(_seed.AliceMailbox, FolderKind.Inbox, "Inbox", 100);
        using (IServiceScope scope = _host.Scope())
        {
            MailFolder sub = await scope.ServiceProvider.GetRequiredService<FolderService>().EnsureAsync(_seed.AliceMailbox.Id, "INBOX/Clients");
            await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(sub.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "Client", new string('y', 3000))));
        }

        using IServiceScope check = _host.Scope();
        IReadOnlyList<FolderUsage> folders = await check.ServiceProvider.GetRequiredService<MailboxUsageService>().GetFoldersAsync(_seed.AliceMailbox.Id);

        Assert.Equal(new[] { "INBOX", "INBOX/Clients", "Drafts", "Sent", "Archive", "Junk", "Trash" }, folders.Select(f => f.Path));
        Assert.Equal((1L, inbox.SizeBytes), (folders[0].Messages, folders[0].Bytes));
        Assert.Equal(1, folders[1].Messages);
        Assert.True(folders[1].Bytes > 3000);
        Assert.All(folders.Skip(2), f => Assert.Equal((0L, 0L), (f.Messages, f.Bytes)));
    }
}

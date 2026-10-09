using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>Conversations: the messages of a thread as one row of the list and as one stack in the reader.</summary>
public class ConversationTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private long _inbox;
    private long _archive;
    private long _trash;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var folders = await db.MailFolders.IgnoreQueryFilters().Where(f => f.MailboxId == _seed.AliceMailbox.Id).ToListAsync();
        _inbox = folders.Single(f => f.Kind == FolderKind.Inbox).Id;
        _archive = folders.Single(f => f.Kind == FolderKind.Archive).Id;
        _trash = folders.Single(f => f.Kind == FolderKind.Trash).Id;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Delivers a message to alice; a reply names the message it answers (and the first one of the chain).</summary>
    private async Task<long> DeliverAsync(string from, string subject, string messageId, string? inReplyTo = null, string? root = null)
    {
        string headers = inReplyTo is null ? string.Empty : $"In-Reply-To: {inReplyTo}\r\nReferences: {root ?? inReplyTo}{(root is null ? string.Empty : " " + inReplyTo)}\r\n";
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build(from, "alice@example.test", subject, "Text of " + subject, messageId, headers), new DeliverySource { EnvelopeRecipients = ["alice@example.test"] });
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        return await db.MailMessages.IgnoreQueryFilters().Where(m => m.MessageIdHeader == messageId && m.MailboxId == _seed.AliceMailbox.Id).Select(m => m.Id).SingleAsync();
    }

    private async Task MoveAsync(long messageId, long folderId)
    {
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MailStore>().MoveAsync([messageId], folderId);
    }

    private async Task<ConversationPage> ListAsync(long folderId, int? page = null, int size = 50)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailQuery query = MailQuery.Parse(_seed.AliceMailbox.Id, folderId, null);
        return await MailConversations.ListAsync(query.Apply(db.MailMessages.AsNoTracking(), db), page, size, CancellationToken.None);
    }

    private async Task<IReadOnlyList<ThreadMessage>> ThreadOfAsync(long messageId)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailMessage message = await db.MailMessages.AsNoTracking().SingleAsync(m => m.Id == messageId);
        FolderKind kind = await db.MailFolders.IgnoreQueryFilters().Where(f => f.Id == message.FolderId).Select(f => f.Kind).SingleAsync();
        return await MailConversations.ThreadAsync(db, message, kind, CancellationToken.None);
    }

    [DbFact]
    public async Task The_messages_of_a_thread_are_one_row_that_names_all_of_them()
    {
        long first = await DeliverAsync("bob@sender.test", "Offer", "<a1@sender.test>");
        long second = await DeliverAsync("carol@sender.test", "Re: Offer", "<a2@sender.test>", "<a1@sender.test>");
        long third = await DeliverAsync("bob@sender.test", "AW: Re: Offer", "<a3@sender.test>", "<a2@sender.test>", "<a1@sender.test>");
        long lunch = await DeliverAsync("dave@sender.test", "Lunch", "<b1@sender.test>");

        ConversationPage page = await ListAsync(_inbox);

        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { lunch, third }, page.Rows.Select(r => r.Id).ToArray());           // the one with the newest message first
        ConversationRow offer = page.Rows[1];
        Assert.Equal(new[] { first, second, third }, offer.Ids);                                // oldest first, the newest message is the one of the row
        Assert.Equal("Offer", offer.Subject);                                                   // the subject without "Re:" and "AW:"
        Assert.Equal(3, offer.UnreadCount);
        Assert.False(offer.IsRead);
        Assert.Equal(new[] { "bob@sender.test", "carol@sender.test" }, offer.Participants);   // who wrote, once each
        Assert.Equal(new[] { lunch }, page.Rows[0].Ids);
    }

    [DbFact]
    public async Task A_row_is_read_when_all_its_messages_are_and_starred_when_one_is()
    {
        long first = await DeliverAsync("bob@sender.test", "Offer", "<a1@sender.test>");
        long second = await DeliverAsync("bob@sender.test", "Re: Offer", "<a2@sender.test>", "<a1@sender.test>");
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailMessages.Where(m => m.Id == first).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true).SetProperty(m => m.IsStarred, true));
        }

        ConversationRow row = (await ListAsync(_inbox)).Rows.Single();
        Assert.False(row.IsRead);
        Assert.Equal(1, row.UnreadCount);
        Assert.True(row.IsStarred);

        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailMessages.Where(m => m.Id == second).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true));
        }

        row = (await ListAsync(_inbox)).Rows.Single();
        Assert.True(row.IsRead);
        Assert.Equal(0, row.UnreadCount);
    }

    [DbFact]
    public async Task A_page_is_counted_in_conversations_not_in_messages()
    {
        for (int i = 1; i <= 5; i++)
        {
            long root = await DeliverAsync("bob@sender.test", "Topic " + i, $"<t{i}@sender.test>");
            Assert.True(root > 0);
            await DeliverAsync("carol@sender.test", "Re: Topic " + i, $"<t{i}r@sender.test>", $"<t{i}@sender.test>");
        }

        ConversationPage first = await ListAsync(_inbox, page: 1, size: 2);
        ConversationPage third = await ListAsync(_inbox, page: 3, size: 2);
        ConversationPage beyond = await ListAsync(_inbox, page: 9, size: 2);

        Assert.Equal(5, first.Total);                                          // five conversations of ten messages
        Assert.Equal(new[] { "Topic 5", "Topic 4" }, first.Rows.Select(r => r.Subject).ToArray());
        Assert.All(first.Rows, r => Assert.Equal(2, r.Ids.Length));
        Assert.Single(third.Rows);
        Assert.Equal("Topic 1", third.Rows[0].Subject);
        Assert.Equal(3, beyond.Page);                                          // a page that is not there is the last one
    }

    [DbFact]
    public async Task A_row_only_names_the_messages_that_are_in_its_folder()
    {
        long first = await DeliverAsync("bob@sender.test", "Offer", "<a1@sender.test>");
        long second = await DeliverAsync("bob@sender.test", "Re: Offer", "<a2@sender.test>", "<a1@sender.test>");
        await MoveAsync(first, _archive);

        ConversationRow inInbox = (await ListAsync(_inbox)).Rows.Single();
        ConversationRow inArchive = (await ListAsync(_archive)).Rows.Single();

        Assert.Equal(new[] { second }, inInbox.Ids);
        Assert.Equal(new[] { first }, inArchive.Ids);
    }

    [DbFact]
    public async Task A_message_without_a_key_is_a_conversation_of_its_own()
    {
        long first = await DeliverAsync("bob@sender.test", "Old one", "<o1@sender.test>");
        long second = await DeliverAsync("carol@sender.test", "Older one", "<o2@sender.test>");
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailMessages.Where(m => m.Id == first || m.Id == second).ExecuteUpdateAsync(s => s.SetProperty(m => m.ThreadKey, (string?)null));
        }

        ConversationPage page = await ListAsync(_inbox);

        Assert.Equal(2, page.Total);
        Assert.All(page.Rows, r => Assert.Single(r.Ids));
    }

    [DbFact]
    public async Task The_reader_stacks_the_whole_conversation_oldest_first_across_the_folders_but_not_the_bin()
    {
        long first = await DeliverAsync("bob@sender.test", "Offer", "<a1@sender.test>");
        long second = await DeliverAsync("carol@sender.test", "Re: Offer", "<a2@sender.test>", "<a1@sender.test>");
        long third = await DeliverAsync("bob@sender.test", "Re: Offer", "<a3@sender.test>", "<a2@sender.test>", "<a1@sender.test>");
        long deleted = await DeliverAsync("dave@sender.test", "Re: Offer", "<a4@sender.test>", "<a3@sender.test>", "<a1@sender.test>");
        await DeliverAsync("erin@sender.test", "Unrelated", "<z1@sender.test>");
        await MoveAsync(first, _archive);
        await MoveAsync(deleted, _trash);

        IReadOnlyList<ThreadMessage> fromInbox = await ThreadOfAsync(third);

        Assert.Equal(new[] { first, second, third }, fromInbox.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { FolderKind.Archive, FolderKind.Inbox, FolderKind.Inbox }, fromInbox.Select(t => t.FolderKind).ToArray());
        Assert.Equal("carol@sender.test", fromInbox[1].FromAddress);

        // Opened in the trash, the conversation is what is in the trash.
        Assert.Equal(new[] { deleted }, (await ThreadOfAsync(deleted)).Select(t => t.Id).ToArray());
    }

    [DbFact]
    public async Task A_message_that_the_rules_leave_out_is_still_in_its_own_conversation()
    {
        long only = await DeliverAsync("bob@sender.test", "Offer", "<a1@sender.test>");
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailMessages.Where(m => m.Id == only).ExecuteUpdateAsync(s => s.SetProperty(m => m.ThreadKey, (string?)null));
        }

        Assert.Equal(new[] { only }, (await ThreadOfAsync(only)).Select(t => t.Id).ToArray());
    }
}

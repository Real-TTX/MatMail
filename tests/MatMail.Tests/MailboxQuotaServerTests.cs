using MatMail.Data;
using MatMail.Tests.Support;

namespace MatMail.Tests;

/// <summary>The SMTP server and a full mailbox: a temporary refusal, so that the sender tries again later.</summary>
public class MailboxQuotaSmtpTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private RunningSmtpServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _server = await RunningSmtpServer.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _host.DisposeAsync();
    }

    private static string Text(byte[] raw) => System.Text.Encoding.UTF8.GetString(raw);

    [DbFact]
    public async Task A_full_mailbox_is_refused_at_rcpt_with_a_temporary_error_and_the_other_recipients_still_work()
    {
        await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);

        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<max@sender.test>")).Code);
            Assert.Equal("452 4.2.2 <alice@example.test>: Mailbox full, try again later", (await client.CommandAsync("RCPT TO:<alice@example.test>")).ToString());
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<bob@example.test>")).Code);
            Assert.Equal(354, (await client.CommandAsync("DATA")).Code);
            await client.SendAsync(Text(RawMail.Build("max@sender.test", "alice@example.test, bob@example.test", "For two", "x")) + ".\r\n");
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
        }

        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id));   // the filler only
    }

    [DbFact]
    public async Task A_mailbox_that_filled_up_during_the_transaction_refuses_the_message_for_all_and_the_next_try_works()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            string message = Text(RawMail.Build("max@sender.test", "alice@example.test, bob@example.test", "Race", "x"));

            // Both recipients were fine at RCPT TO; then the mailbox of Alice fills up (another message, a big attachment, ...).
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<max@sender.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<alice@example.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<bob@example.test>")).Code);
            await QuotaTestSupport.FillUpAsync(_host, _seed.AliceMailbox.Id);
            Assert.Equal(354, (await client.CommandAsync("DATA")).Code);
            await client.SendAsync(message + ".\r\n");
            Assert.Equal("452 4.2.2 Mailbox full, try again later", (await client.ReadReplyAsync()).ToString());

            // Nothing was delivered to anybody (the sender repeats the whole message), and the connection is still good.
            Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));
            Assert.Equal("250 2.0.0 OK", (await client.CommandAsync("RSET")).ToString());

            await QuotaTestSupport.SetLimitAsync(_host, _seed.AliceMailbox.Id, null);
            Assert.Equal(250, (await client.SendMailAsync("max@sender.test", new[] { "alice@example.test", "bob@example.test" }, message)).Code);
        }

        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));
        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(_host, _seed.AliceMailbox.Id)).Count);
    }

    [DbFact]
    public async Task A_mail_program_gets_the_same_temporary_refusal_for_a_full_mailbox()
    {
        _host.Config.Smtp.RequireTlsForAuth = false;
        await QuotaTestSupport.FillUpAsync(_host, _seed.BobMailbox.Id);

        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.SubmissionPort);
        await using (client)
        {
            await client.EhloAsync();
            string plain = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"\0alice\0{ImapTestServer.Password}"));
            Assert.Equal(235, (await client.CommandAsync("AUTH PLAIN " + plain)).Code);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<alice@example.test>")).Code);
            Assert.Equal("452 4.2.2 <bob@example.test>: Mailbox full, try again later", (await client.CommandAsync("RCPT TO:<bob@example.test>")).ToString());
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<info@example.test>")).Code);   // the rest of the recipients may still be sent to

            // Bob's mailbox fills up only now for the other one: the message is refused as a whole, nothing goes to Info either.
            await QuotaTestSupport.SetLimitAsync(_host, _seed.BobMailbox.Id, null);
            Assert.Equal(250, (await client.CommandAsync("RSET")).Code);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<alice@example.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<bob@example.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<info@example.test>")).Code);
            await QuotaTestSupport.SetLimitAsync(_host, _seed.BobMailbox.Id, await QuotaTestSupport.UsedAsync(_host, _seed.BobMailbox.Id));
            Assert.Equal(354, (await client.CommandAsync("DATA")).Code);
            await client.SendAsync(Text(RawMail.Build("Alice <alice@example.test>", "bob@example.test, info@example.test", "Race", "x")) + ".\r\n");
            SmtpReplyLines refused = await client.ReadReplyAsync();
            Assert.Equal(452, refused.Code);
            Assert.StartsWith("4.2.2 A recipient's mailbox is full", refused.Text);
        }

        Assert.Empty(await QuotaTestSupport.MessagesAsync(_host, _seed.Info.Id));
    }
}

/// <summary>The IMAP server and a full mailbox: no new messages (APPEND, COPY, MOVE into it); deleting and moving inside always work.</summary>
public class MailboxQuotaImapTests : ImapTestBase
{
    private const string Overquota = "[OVERQUOTA] The mailbox is full: it takes no new messages until something is deleted";

    private static byte[] Small() => System.Text.Encoding.UTF8.GetBytes("Subject: x\r\n\r\nHello\r\n");

    private static async Task<string> AppendAsync(RawImapClient raw, string tag, string mailbox)
    {
        byte[] message = Small();
        await raw.SendAsync($"{tag} APPEND {mailbox} {{{message.Length}+}}\r\n");
        await raw.SendAsync(message);
        await raw.SendAsync("\r\n");
        return (await raw.ReadUntilTaggedAsync(tag))[^1];
    }

    [DbFact]
    public async Task A_full_mailbox_takes_no_new_messages_but_the_owner_can_move_inside_and_delete_to_make_room()
    {
        await AddToInboxAsync(ImapTestData.Simple("Filler", new string('x', 2000)));
        await QuotaTestSupport.SetLimitAsync(Host, AliceId, await QuotaTestSupport.UsedAsync(Host, AliceId));

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal($"a1 NO {Overquota}", await AppendAsync(raw, "a1", "INBOX"));
        Assert.Equal($"a2 NO {Overquota}", await AppendAsync(raw, "a2", "Archive"));

        await raw.CommandAsync("s", "SELECT INBOX");
        Assert.Equal($"a3 NO {Overquota}", (await raw.CommandAsync("a3", "COPY 1 Archive"))[^1]);
        Assert.Equal($"a4 NO {Overquota}", (await raw.CommandAsync("a4", "UID COPY 1:* Archive"))[^1]);
        Assert.Single(await QuotaTestSupport.MessagesAsync(Host, AliceId));
        Assert.Empty(await QuotaTestSupport.MessagesAsync(Host, AliceId, FolderKind.Archive));

        // Inside the mailbox nothing is added: moving works.
        Assert.StartsWith("a5 OK", (await raw.CommandAsync("a5", "MOVE 1 Archive"))[^1]);
        Assert.Single(await QuotaTestSupport.MessagesAsync(Host, AliceId, FolderKind.Archive));

        // A mail program without MOVE deletes by copying into the trash, marking as deleted and expunging: that copy is allowed.
        await raw.CommandAsync("s1", "SELECT Archive");
        Assert.StartsWith("a5b OK", (await raw.CommandAsync("a5b", "COPY 1 Trash"))[^1]);
        Assert.Single(await QuotaTestSupport.MessagesAsync(Host, AliceId, FolderKind.Trash));
        Assert.Equal($"a5c NO {Overquota}", (await raw.CommandAsync("a5c", "COPY 1 INBOX"))[^1]);   // anywhere else it is refused

        // Deleting works, and it is what makes room - for good: what is in the trash still takes room until it is deleted there too.
        await raw.CommandAsync("s2", "SELECT Archive");
        Assert.StartsWith("a6 OK", (await raw.CommandAsync("a6", "STORE 1 +FLAGS (\\Deleted)"))[^1]);
        Assert.StartsWith("a7 OK", (await raw.CommandAsync("a7", "EXPUNGE"))[^1]);
        Assert.Equal($"a7b NO {Overquota}", await AppendAsync(raw, "a7b", "INBOX"));
        await raw.CommandAsync("s3", "SELECT Trash");
        Assert.StartsWith("a7c OK", (await raw.CommandAsync("a7c", "STORE 1 +FLAGS (\\Deleted)"))[^1]);
        Assert.StartsWith("a7d OK", (await raw.CommandAsync("a7d", "EXPUNGE"))[^1]);
        Assert.StartsWith("a8 OK [APPENDUID ", await AppendAsync(raw, "a8", "INBOX"));
    }

    [DbFact]
    public async Task A_full_shared_mailbox_takes_no_copies_moves_or_appended_messages_from_others()
    {
        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Alice, MailboxAccess.Send);
        await AddToInboxAsync(ImapTestData.Simple("For the team"));
        await QuotaTestSupport.FillUpAsync(Host, Seed.Info.Id);

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        Assert.Equal($"a1 NO {Overquota}", (await raw.CommandAsync("a1", "COPY 1 Shared/Info/INBOX"))[^1]);
        Assert.Equal($"a2 NO {Overquota}", (await raw.CommandAsync("a2", "MOVE 1 Shared/Info/INBOX"))[^1]);
        Assert.Equal($"a3 NO {Overquota}", await AppendAsync(raw, "a3", "Shared/Info/Sent"));
        Assert.Single(await QuotaTestSupport.MessagesAsync(Host, AliceId));
        Assert.Single(await QuotaTestSupport.MessagesAsync(Host, Seed.Info.Id));

        // The limit is raised: the same move works.
        await QuotaTestSupport.SetLimitAsync(Host, Seed.Info.Id, null);
        Assert.StartsWith("a4 OK", (await raw.CommandAsync("a4", "MOVE 1 Shared/Info/INBOX"))[^1]);
        Assert.Equal(2, (await QuotaTestSupport.MessagesAsync(Host, Seed.Info.Id)).Count);
        Assert.Empty(await QuotaTestSupport.MessagesAsync(Host, AliceId));
    }
}

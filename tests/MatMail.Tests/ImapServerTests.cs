using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MatMail.Data;
using MatMail.MailServer.Imap;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MailFolder = MatMail.Data.MailFolder;
using MailStore = MatMail.Messaging.MailStore;

namespace MatMail.Tests;

/// <summary>
/// The IMAP tests run against a fresh database with the seed data (alice, bob, info@) and an IMAP server on ephemeral loopback
/// ports. They are split into several classes so xUnit runs them in parallel.
/// </summary>
public abstract class ImapTestBase : IAsyncLifetime
{
    protected TestHost Host { get; private set; } = null!;

    protected Seed Seed { get; private set; } = null!;

    protected ImapTestServer Imap { get; private set; } = null!;

    protected long AliceId => Seed.AliceMailbox.Id;

    public async Task InitializeAsync()
    {
        Host = await TestHost.CreateAsync(Configure);
        Seed = await Host.SeedAsync();
        Imap = await ImapTestServer.StartAsync(Host, WithCertificate);
    }

    public async Task DisposeAsync()
    {
        await Imap.DisposeAsync();
        await Host.DisposeAsync();
    }

    protected virtual bool WithCertificate => true;

    protected virtual void Configure(Configuration.AppConfig config)
    {
    }

    protected Task<MailMessage> AddToInboxAsync(byte[] raw, Func<NewMessage, NewMessage>? shape = null)
        => ImapTestData.AddAsync(Host, AliceId, FolderKind.Inbox, raw, shape);

    /// <summary>A raw client over implicit TLS, signed in as alice (greeting and LOGIN already read).</summary>
    protected async Task<RawImapClient> LoginRawAsync(string login = "alice")
    {
        RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();
        List<string> response = await raw.CommandAsync("L0", $"LOGIN {login} {ImapTestServer.Password}");
        Assert.StartsWith("L0 OK", response[^1]);
        return raw;
    }

    protected static string PlainResponse(string login, string password)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{login}\0{password}"));
}

/// <summary>Connections, TLS (STARTTLS and implicit), sign-in, throttling, limits, timeouts and shutdown.</summary>
public class ImapSignInTests : ImapTestBase
{
    [DbFact]
    public async Task Starttls_upgrades_the_connection_and_advertises_the_extensions()
    {
        using ImapClient client = await Imap.ConnectAsync(SecureSocketOptions.StartTls);
        Assert.True(client.IsSecure);
        Assert.Contains("PLAIN", client.AuthenticationMechanisms);
        foreach (ImapCapabilities capability in new[]
                 {
                     ImapCapabilities.IMAP4rev1, ImapCapabilities.Idle, ImapCapabilities.UidPlus, ImapCapabilities.Move, ImapCapabilities.Namespace,
                     ImapCapabilities.SpecialUse, ImapCapabilities.Children, ImapCapabilities.Unselect, ImapCapabilities.LiteralPlus, ImapCapabilities.SaslIR,
                     ImapCapabilities.Id, ImapCapabilities.Enable, ImapCapabilities.ListExtended, ImapCapabilities.ListStatus,
                 })
        {
            Assert.True(client.Capabilities.HasFlag(capability), $"{capability} is not advertised");
        }

        await client.AuthenticateAsync("alice", ImapTestServer.Password);
        Assert.True(client.IsAuthenticated);
        Assert.Equal("INBOX", client.Inbox.FullName);
        Assert.Equal("Shared/", client.SharedNamespaces.Single().Path + "/");
    }

    [DbFact]
    public async Task Implicit_tls_works_from_the_first_byte()
    {
        using ImapClient client = await Imap.LoginAsync(security: SecureSocketOptions.SslOnConnect);
        Assert.True(client.IsSecure);
        Assert.True(client.IsAuthenticated);
    }

    [DbFact]
    public async Task Without_tls_login_is_disabled_until_starttls()
    {
        await using RawImapClient raw = await Imap.RawAsync();
        string greeting = await raw.ReadLineAsync();
        Assert.StartsWith("* OK [CAPABILITY IMAP4rev1 ", greeting);
        Assert.Contains(" STARTTLS ", greeting);
        Assert.Contains(" LOGINDISABLED]", greeting);
        Assert.DoesNotContain("AUTH=PLAIN", greeting);

        Assert.Equal("a1 NO [PRIVACYREQUIRED] Log in over an encrypted connection (use STARTTLS)", (await raw.CommandAsync("a1", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
        Assert.StartsWith("a2 NO [PRIVACYREQUIRED]", (await raw.CommandAsync("a2", "AUTHENTICATE PLAIN " + PlainResponse("alice", ImapTestServer.Password)))[^1]);

        Assert.Equal(new[] { "a3 OK Begin TLS negotiation now" }, await raw.CommandAsync("a3", "STARTTLS"));
        await raw.StartTlsAsync();
        List<string> capability = await raw.CommandAsync("a4", "CAPABILITY");
        Assert.Contains("AUTH=PLAIN", capability[0]);
        Assert.DoesNotContain("STARTTLS", capability[0]);
        Assert.DoesNotContain("LOGINDISABLED", capability[0]);
        Assert.Equal("a5 BAD Please log in first", (await raw.CommandAsync("a5", "SELECT INBOX"))[^1]);
        Assert.StartsWith("a6 OK [CAPABILITY IMAP4rev1 ", (await raw.CommandAsync("a6", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
        Assert.Equal("a7 BAD Already logged in", (await raw.CommandAsync("a7", "STARTTLS"))[^1]);
    }

    [DbFact]
    public async Task Commands_sent_in_the_clear_after_starttls_are_not_executed()
    {
        await using RawImapClient raw = await Imap.RawAsync();
        await raw.ReadLineAsync();

        // An attacker in the middle appends a command to the client's STARTTLS; it must not run inside the encrypted session.
        await raw.SendAsync("a1 STARTTLS\r\na2 LOGIN alice " + ImapTestServer.Password + "\r\n");
        Assert.Equal("a1 OK Begin TLS negotiation now", await raw.ReadLineAsync());
        await raw.StartTlsAsync();
        Assert.Equal(new[] { "a3 BAD Please log in first" }, await raw.CommandAsync("a3", "SELECT INBOX"));
    }

    [DbFact]
    public async Task A_wrong_password_is_refused_and_the_client_may_try_again()
    {
        using ImapClient client = await Imap.ConnectAsync();
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("alice", "wrong-password"));
        Assert.True(client.IsConnected);

        await client.AuthenticateAsync("alice", ImapTestServer.Password);
        Assert.True(client.IsAuthenticated);
    }

    [DbFact]
    public async Task Authenticate_plain_works_with_and_without_an_initial_response()
    {
        await using RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();
        await raw.SendAsync("a1 AUTHENTICATE PLAIN\r\n");
        Assert.Equal("+ ", await raw.ReadLineAsync());
        await raw.SendAsync(PlainResponse("alice", ImapTestServer.Password) + "\r\n");
        Assert.StartsWith("a1 OK [CAPABILITY IMAP4rev1", await raw.ReadLineAsync());

        await using RawImapClient second = await Imap.RawAsync(implicitTls: true);
        await second.ReadLineAsync();
        Assert.StartsWith("b1 OK", (await second.CommandAsync("b1", "AUTHENTICATE PLAIN " + PlainResponse("bob", ImapTestServer.Password)))[^1]);

        await using RawImapClient third = await Imap.RawAsync(implicitTls: true);
        await third.ReadLineAsync();
        await third.SendAsync("c1 AUTHENTICATE PLAIN\r\n");
        Assert.Equal("+ ", await third.ReadLineAsync());
        await third.SendAsync("*\r\n");
        Assert.Equal("c1 BAD Authentication cancelled", await third.ReadLineAsync());
        Assert.Equal("c2 NO [CANNOT] Unsupported authentication mechanism", (await third.CommandAsync("c2", "AUTHENTICATE CRAM-MD5"))[^1]);
        Assert.Equal("c3 NO [AUTHENTICATIONFAILED] Invalid credentials", (await third.CommandAsync("c3", "AUTHENTICATE PLAIN " + PlainResponse("alice", "nope")))[^1]);
    }

    [DbFact]
    public async Task Repeated_failed_logins_close_the_connection_and_block_the_address()
    {
        Imap.Throttle.BlockAfterFailures = 6;
        await using (RawImapClient raw = await Imap.RawAsync(implicitTls: true))
        {
            await raw.ReadLineAsync();
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                Assert.Equal($"a{attempt} NO [AUTHENTICATIONFAILED] Invalid credentials", (await raw.CommandAsync($"a{attempt}", "LOGIN nobody wrong"))[^1]);
            }

            Assert.Equal("* BYE Too many failed login attempts", await raw.ReadLineAsync());
            Assert.True(await raw.IsClosedAsync());
        }

        await using (RawImapClient raw = await Imap.RawAsync(implicitTls: true))
        {
            await raw.ReadLineAsync();
            Assert.Equal("b1 NO [AUTHENTICATIONFAILED] Invalid credentials", (await raw.CommandAsync("b1", "LOGIN nobody wrong"))[^1]);
            Assert.Equal("* BYE Too many failed login attempts", await raw.ReadLineAsync());
        }

        // Now the name is blocked from this address: even a right password would not be looked at ...
        await using RawImapClient blocked = await Imap.RawAsync(implicitTls: true);
        await blocked.ReadLineAsync();
        Assert.Equal("c1 NO [UNAVAILABLE] Too many failed logins from your address; try again later", (await blocked.CommandAsync("c1", $"LOGIN nobody {ImapTestServer.Password}"))[^1]);
        Assert.Equal("* BYE Too many failed login attempts", await blocked.ReadLineAsync());

        // ... while everybody else behind the same address is not locked out with it.
        await using RawImapClient colleague = await Imap.RawAsync(implicitTls: true);
        await colleague.ReadLineAsync();
        Assert.StartsWith("d1 OK", (await colleague.CommandAsync("d1", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
    }

    [DbFact]
    public async Task An_address_that_fails_at_many_names_is_slowed_down_but_not_locked_out()
    {
        Imap.Throttle.PenalizeAfterFailures = 4;
        Imap.Throttle.PenaltyDelay = TimeSpan.FromMilliseconds(400);
        for (int i = 0; i < 4; i++)
        {
            Imap.Throttle.RecordFailure("127.0.0.1", "guess" + i);
        }

        await using RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), "The sign-in was not delayed.");
    }

    [DbFact]
    public async Task Too_many_connections_from_one_address_are_refused()
    {
        Host.Config.Imap.MaxConnectionsPerIp = 2;
        await using RawImapClient first = await Imap.RawAsync();
        await using RawImapClient second = await Imap.RawAsync();
        Assert.StartsWith("* OK", await first.ReadLineAsync());
        Assert.StartsWith("* OK", await second.ReadLineAsync());

        await using RawImapClient third = await Imap.RawAsync();
        Assert.Equal("* BYE Too many connections from your address", await third.ReadLineAsync());
    }

    [DbFact]
    public async Task An_idle_session_is_logged_out_after_the_timeout()
    {
        Imap.Server.IdleTimeout = TimeSpan.FromMilliseconds(500);
        await using RawImapClient raw = await Imap.RawAsync();
        await raw.ReadLineAsync();
        Assert.Equal("* BYE Autologout; idle for too long", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task Broken_connections_do_not_disturb_the_listener()
    {
        // Text instead of a TLS handshake on the TLS port: the server just drops the connection.
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", Imap.TlsPort);
            await tcp.GetStream().WriteAsync("a1 CAPABILITY\r\n"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                Assert.Equal(0, await tcp.GetStream().ReadAsync(new byte[256], timeout.Token));
            }
            catch (IOException)
            {
                // A reset is fine as well.
            }
        }

        // A client that disappears in the middle of a literal.
        await using (RawImapClient vanishing = await Imap.RawAsync())
        {
            await vanishing.ReadLineAsync();
            await vanishing.SendAsync("a1 LOGIN {5}\r\n");
            Assert.Equal("+ Ready for literal data", await vanishing.ReadLineAsync());
        }

        using ImapClient client = await Imap.LoginAsync(security: SecureSocketOptions.SslOnConnect);
        Assert.True(client.IsAuthenticated);
    }

    [DbFact]
    public async Task Stopping_the_server_says_goodbye_to_every_session()
    {
        await using RawImapClient raw = await LoginRawAsync();
        await Imap.Server.StopAsync(CancellationToken.None);
        Assert.Equal("* BYE Server shutting down", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

}

/// <summary>LIST/LSUB (with LIST-EXTENDED), CREATE, RENAME, DELETE, SUBSCRIBE, SELECT/EXAMINE and STATUS.</summary>
public class ImapFolderTests : ImapTestBase
{
    [DbFact]
    public async Task List_shows_special_use_nested_and_umlaut_folders()
    {
        await ImapTestData.CreateFolderAsync(Host, AliceId, "Projekte/Bestätigungen");
        await ImapTestData.CreateFolderAsync(Host, AliceId, "Ärger & Co");

        using ImapClient client = await Imap.LoginAsync();
        IList<IMailFolder> folders = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        Assert.Equal(
            new[] { "Archive", "Drafts", "INBOX", "Junk", "Projekte", "Projekte/Bestätigungen", "Sent", "Trash", "Ärger & Co" },
            folders.Select(f => f.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("Sent", client.GetFolder(SpecialFolder.Sent)!.FullName);
        Assert.Equal("Drafts", client.GetFolder(SpecialFolder.Drafts)!.FullName);
        Assert.Equal("Trash", client.GetFolder(SpecialFolder.Trash)!.FullName);
        Assert.Equal("Junk", client.GetFolder(SpecialFolder.Junk)!.FullName);
        Assert.Equal("Archive", client.GetFolder(SpecialFolder.Archive)!.FullName);
        Assert.True(folders.Single(f => f.FullName == "Projekte").Attributes.HasFlag(FolderAttributes.HasChildren));
        Assert.True(folders.Single(f => f.FullName == "Projekte/Bestätigungen").Attributes.HasFlag(FolderAttributes.HasNoChildren));

        // On the wire, names are modified UTF-7.
        await using RawImapClient raw = await LoginRawAsync();
        List<string> list = await raw.CommandAsync("a1", "LIST \"\" \"*\"");
        Assert.Contains("* LIST (\\HasNoChildren) \"/\" \"INBOX\"", list);
        Assert.Contains("* LIST (\\HasNoChildren \\Sent) \"/\" \"Sent\"", list);
        Assert.Contains("* LIST (\\HasChildren) \"/\" \"Projekte\"", list);
        Assert.Contains("* LIST (\\HasNoChildren) \"/\" \"Projekte/Best&AOQ-tigungen\"", list);
        Assert.Contains("* LIST (\\HasNoChildren) \"/\" \"&AMQ-rger &- Co\"", list);
        Assert.Equal("a1 OK LIST completed", list[^1]);

        List<string> top = await raw.CommandAsync("a2", "LIST \"\" \"%\"");
        Assert.DoesNotContain(top, l => l.Contains("Best&AOQ-tigungen"));
        Assert.Equal(new[] { "* LIST (\\Noselect) \"/\" \"\"", "a3 OK LIST completed" }, await raw.CommandAsync("a3", "LIST \"\" \"\""));
        Assert.Equal(new[] { "* LIST (\\HasNoChildren) \"/\" \"Projekte/Best&AOQ-tigungen\"", "a4 OK LIST completed" }, await raw.CommandAsync("a4", "LIST \"Projekte/\" \"Best&AOQ-tigungen\""));
    }

    [DbFact]
    public async Task List_extended_selects_subscribed_and_special_use_folders()
    {
        await ImapTestData.CreateFolderAsync(Host, AliceId, "Kunden/Meier");
        await using RawImapClient raw = await LoginRawAsync();
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "UNSUBSCRIBE Kunden"))[^1]);

        List<string> special = await raw.CommandAsync("a2", "LIST (SPECIAL-USE) \"\" \"*\"");
        Assert.Equal(6, special.Count);
        Assert.Contains("* LIST (\\HasNoChildren \\Trash) \"/\" \"Trash\"", special);

        List<string> subscribed = await raw.CommandAsync("a3", "LIST (SUBSCRIBED) \"\" \"*\"");
        Assert.Contains("* LIST (\\HasNoChildren \\Subscribed) \"/\" \"Kunden/Meier\"", subscribed);
        Assert.DoesNotContain(subscribed, l => l.EndsWith("\"Kunden\""));

        List<string> recursive = await raw.CommandAsync("a4", "LIST (SUBSCRIBED RECURSIVEMATCH) \"\" \"%\"");
        Assert.Contains("* LIST (\\HasChildren) \"/\" \"Kunden\" (\"CHILDINFO\" (\"SUBSCRIBED\"))", recursive);

        List<string> withStatus = await raw.CommandAsync("a5", "LIST \"\" (\"INBOX\" \"Kunden/*\") RETURN (SUBSCRIBED CHILDREN STATUS (MESSAGES UNSEEN))");
        Assert.Equal(
            new[]
            {
                "* LIST (\\HasNoChildren \\Subscribed) \"/\" \"INBOX\"",
                "* STATUS \"INBOX\" (MESSAGES 0 UNSEEN 0)",
                "* LIST (\\HasNoChildren \\Subscribed) \"/\" \"Kunden/Meier\"",
                "* STATUS \"Kunden/Meier\" (MESSAGES 0 UNSEEN 0)",
                "a5 OK LIST completed",
            },
            withStatus);

        Assert.StartsWith("a6 BAD", (await raw.CommandAsync("a6", "LIST (RECURSIVEMATCH) \"\" \"*\""))[^1]);
        Assert.StartsWith("a7 BAD", (await raw.CommandAsync("a7", "LIST (FANCY) \"\" \"*\""))[^1]);

        List<string> lsub = await raw.CommandAsync("a8", "LSUB \"\" \"%\"");
        Assert.Contains("* LSUB (\\Noselect) \"/\" \"Kunden\"", lsub);
        Assert.Contains("* LSUB (\\HasNoChildren) \"/\" \"INBOX\"", lsub);
        List<string> lsubAll = await raw.CommandAsync("a9", "LSUB \"\" \"*\"");
        Assert.Contains("* LSUB (\\HasNoChildren) \"/\" \"Kunden/Meier\"", lsubAll);
        Assert.DoesNotContain(lsubAll, l => l.EndsWith("\"Kunden\""));
    }

    [DbFact]
    public async Task Unsubscribed_folders_disappear_from_the_subscribed_list()
    {
        using ImapClient client = await Imap.LoginAsync();
        IMailFolder archive = await client.GetFolderAsync("Archive");
        await archive.UnsubscribeAsync();

        IList<IMailFolder> subscribed = await client.GetFoldersAsync(client.PersonalNamespaces[0], StatusItems.None, subscribedOnly: true);
        Assert.DoesNotContain(subscribed, f => f.FullName == "Archive");
        Assert.Contains(subscribed, f => f.FullName == "INBOX");
        Assert.False((await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive)).IsSubscribed);

        await archive.SubscribeAsync();
        Assert.True((await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive)).IsSubscribed);
    }

    [DbFact]
    public async Task Folders_can_be_created_renamed_and_deleted()
    {
        using ImapClient client = await Imap.LoginAsync();
        IMailFolder root = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder travel = (await root.CreateAsync("Reisen", true))!;
        IMailFolder year = (await travel.CreateAsync("2026", true))!;
        Assert.Equal("Reisen/2026", year.FullName);

        await travel.RenameAsync(root, "Urlaub");
        IList<IMailFolder> folders = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        Assert.Contains(folders, f => f.FullName == "Urlaub/2026");
        Assert.DoesNotContain(folders, f => f.FullName.StartsWith("Reisen", StringComparison.Ordinal));

        await using (RawImapClient raw = await LoginRawAsync())
        {
            Assert.Equal("a1 NO [ALREADYEXISTS] Mailbox already exists", (await raw.CommandAsync("a1", "CREATE Urlaub"))[^1]);
            Assert.Equal("a2 NO [CANNOT] System folders cannot be deleted", (await raw.CommandAsync("a2", "DELETE Trash"))[^1]);
            Assert.Equal("a3 NO [CANNOT] The folder has subfolders; delete them first", (await raw.CommandAsync("a3", "DELETE Urlaub"))[^1]);
            Assert.Equal("a4 NO [NONEXISTENT] No such mailbox", (await raw.CommandAsync("a4", "DELETE Nirgendwo"))[^1]);
            Assert.Equal("a5 NO [CANNOT] System folders cannot be renamed.", (await raw.CommandAsync("a5", "RENAME Drafts Entw&APw-rfe"))[^1]);
            Assert.Equal("a6 NO [ALREADYEXISTS] The new name already exists", (await raw.CommandAsync("a6", "RENAME Urlaub Sent"))[^1]);
            Assert.Equal("a7 OK CREATE completed", (await raw.CommandAsync("a7", "CREATE Entw&APw-rfe/Alt/"))[^1]);
            Assert.Equal("a8 NO [CANNOT] Folders can only be created inside a shared mailbox (Shared/<mailbox>/<folder>)", (await raw.CommandAsync("a8", "CREATE Shared"))[^1]);
        }

        Assert.NotNull(await ImapTestData.FolderAsync(Host, AliceId, "Entwürfe/Alt"));
        await year.DeleteAsync();
        await travel.DeleteAsync();
        folders = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        Assert.DoesNotContain(folders, f => f.FullName.StartsWith("Urlaub", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task Renaming_the_inbox_moves_its_messages_into_a_new_folder()
    {
        await AddToInboxAsync(ImapTestData.Simple("Old mail 1"));
        await AddToInboxAsync(ImapTestData.Simple("Old mail 2"));

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal("a1 OK RENAME completed", (await raw.CommandAsync("a1", "RENAME INBOX \"Old Inbox\""))[^1]);

        MailFolder inbox = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);
        MailFolder old = await ImapTestData.FolderAsync(Host, AliceId, "Old Inbox");
        Assert.Empty(await ImapTestData.MessagesAsync(Host, inbox.Id));
        Assert.Equal(new[] { "Old mail 1", "Old mail 2" }, (await ImapTestData.MessagesAsync(Host, old.Id)).Select(m => m.Subject));
    }

    [DbFact]
    public async Task Select_and_examine_report_counts_uidvalidity_and_uidnext()
    {
        await AddToInboxAsync(ImapTestData.Simple("One"), m => m with { IsRead = true });
        await AddToInboxAsync(ImapTestData.Simple("Two"));
        await AddToInboxAsync(ImapTestData.Simple("Three"), m => m with { Keywords = new[] { "$Label2" } });
        MailFolder inbox = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);

        using ImapClient client = await Imap.LoginAsync();
        Assert.Equal(FolderAccess.ReadWrite, await client.Inbox.OpenAsync(FolderAccess.ReadWrite));
        Assert.Equal(3, client.Inbox.Count);
        Assert.Equal((uint)inbox.UidValidity, client.Inbox.UidValidity);
        Assert.Equal(4u, client.Inbox.UidNext!.Value.Id);
        Assert.Equal(1, client.Inbox.FirstUnread);
        Assert.Equal(0, client.Inbox.Recent);
        Assert.Contains("$Label2", client.Inbox.AcceptedKeywords);
        Assert.True(client.Inbox.PermanentFlags.HasFlag(MessageFlags.UserDefined));

        await client.Inbox.CloseAsync();
        Assert.Equal(FolderAccess.ReadOnly, await client.Inbox.OpenAsync(FolderAccess.ReadOnly));
        Assert.Equal(3, client.Inbox.Count);

        await using RawImapClient raw = await LoginRawAsync();
        List<string> select = await raw.CommandAsync("a1", "SELECT inbox");
        Assert.Equal(
            new[]
            {
                "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded $Label2)",
                "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded $Label2 \\*)] Flags permitted",
                "* 3 EXISTS",
                "* 0 RECENT",
                "* OK [UNSEEN 2] Message 2 is the first unseen",
                $"* OK [UIDVALIDITY {inbox.UidValidity}] UIDs valid",
                "* OK [UIDNEXT 4] Predicted next UID",
                "a1 OK [READ-WRITE] SELECT completed",
            },
            select);
        List<string> examine = await raw.CommandAsync("a2", "EXAMINE INBOX");
        Assert.Contains("* OK [PERMANENTFLAGS ()] No permanent flags permitted", examine);
        Assert.Equal("a2 OK [READ-ONLY] EXAMINE completed", examine[^1]);
        Assert.Equal("a3 NO [NONEXISTENT] No such mailbox", (await raw.CommandAsync("a3", "SELECT Nowhere"))[^1]);
        Assert.Equal("a4 BAD No mailbox selected", (await raw.CommandAsync("a4", "FETCH 1 FLAGS"))[^1]);
    }

    [DbFact]
    public async Task Status_reports_messages_unseen_uidnext_and_uidvalidity()
    {
        await ImapTestData.AddAsync(Host, AliceId, FolderKind.Archive, ImapTestData.Simple("Archived 1"), m => m with { IsRead = true });
        await ImapTestData.AddAsync(Host, AliceId, FolderKind.Archive, ImapTestData.Simple("Archived 2"));
        MailFolder archiveRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive);

        using ImapClient client = await Imap.LoginAsync();
        IMailFolder archive = await client.GetFolderAsync("Archive");
        await archive.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidNext | StatusItems.UidValidity | StatusItems.Recent);
        Assert.Equal(2, archive.Count);
        Assert.Equal(1, archive.Unread);
        Assert.Equal(0, archive.Recent);
        Assert.Equal(3u, archive.UidNext!.Value.Id);
        Assert.Equal((uint)archiveRow.UidValidity, archive.UidValidity);

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal(
            new[] { "* STATUS \"Archive\" (UIDNEXT 3 MESSAGES 2 UNSEEN 1)", "a1 OK STATUS completed" },
            await raw.CommandAsync("a1", "STATUS archive (UIDNEXT MESSAGES UNSEEN)"));
        Assert.StartsWith("a2 BAD", (await raw.CommandAsync("a2", "STATUS Archive (SIZE)"))[^1]);
        Assert.Equal("a3 NO [NONEXISTENT] No such mailbox", (await raw.CommandAsync("a3", "STATUS Nowhere (MESSAGES)"))[^1]);
    }

}

/// <summary>FETCH: envelope, body structure, body sections, partial fetches, header fields, UID ranges.</summary>
public class ImapFetchTests : ImapTestBase
{
    [DbFact]
    public async Task Fetch_returns_envelope_and_bodystructure_of_a_multipart_message()
    {
        MailMessage stored = await AddToInboxAsync(ImapTestData.MultipartWithAttachment());

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        IMessageSummary summary = (await client.Inbox.FetchAsync(0, -1,
            MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.UniqueId | MessageSummaryItems.Size |
            MessageSummaryItems.InternalDate | MessageSummaryItems.Flags)).Single();

        Assert.Equal(stored.Uid, summary.UniqueId.Id);
        Assert.Equal(stored.SizeBytes, (long)summary.Size!.Value);
        Assert.Equal(stored.ReceivedDate, summary.InternalDate!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal("Bericht", summary.Envelope!.Subject);
        MailboxAddress from = summary.Envelope.From.Mailboxes.Single();
        Assert.Equal("Jürgen Müller", from.Name);
        Assert.Equal("juergen@sender.test", from.Address);
        Assert.Equal("juergen@sender.test", summary.Envelope.Sender.Mailboxes.Single().Address);
        Assert.Equal("alice@example.test", summary.Envelope.To.Mailboxes.Single().Address);
        Assert.Equal("bob@example.test", summary.Envelope.Cc.Mailboxes.Single().Address);
        Assert.Equal("multi@sender.test", summary.Envelope.MessageId);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(2)), summary.Envelope.Date);

        BodyPartMultipart multipart = Assert.IsType<BodyPartMultipart>(summary.Body);
        Assert.Equal("mixed", multipart.ContentType.MediaSubtype);
        Assert.Equal(2, multipart.BodyParts.Count);
        BodyPartText text = Assert.IsType<BodyPartText>(multipart.BodyParts[0]);
        Assert.Equal("utf-8", text.ContentType.Charset);
        Assert.Equal("1", text.PartSpecifier);
        Assert.Equal(2u, text.Lines);
        BodyPartBasic attachment = Assert.IsType<BodyPartBasic>(multipart.BodyParts[1]);
        Assert.True(attachment.IsAttachment);
        Assert.Equal("bericht.pdf", attachment.FileName);
        Assert.Equal("BASE64", attachment.ContentTransferEncoding, ignoreCase: true);
        Assert.Equal(12u, attachment.Octets);
        Assert.Single(summary.Attachments);

        // Computed once, cached with the message for the next client.
        MailMessageContent content = await ImapTestData.ContentAsync(Host, stored.Id);
        Assert.StartsWith("(\"Tue, 07 Oct 2026 10:00:00 +0200\" \"Bericht\" ((\"=?utf-8?b?", content.EnvelopeImap);
        Assert.StartsWith("((\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"8BIT\"", content.BodyStructureImap);
    }

    [DbFact]
    public async Task Fetch_returns_body_parts_partial_data_and_header_fields_without_marking_seen()
    {
        byte[] raw = ImapTestData.MultipartWithAttachment();
        MailMessage stored = await AddToInboxAsync(raw);
        var uid = new UniqueId((uint)stored.Uid);

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
        IMessageSummary summary = (await client.Inbox.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure)).Single();

        var text = (TextPart)await client.Inbox.GetBodyPartAsync(uid, summary.TextBody!);
        Assert.Equal("Hallo Alice,\r\nanbei der Bericht über Köln.", text.Text);
        var pdf = (MimePart)await client.Inbox.GetBodyPartAsync(uid, summary.Attachments.Single());
        using var decoded = new MemoryStream();
        await pdf.Content!.DecodeToAsync(decoded);
        Assert.Equal("%PDF-1.4\n", Encoding.ASCII.GetString(decoded.ToArray()));

        await using (Stream partial = await client.Inbox.GetStreamAsync(uid, 0, 20))
        {
            using var copy = new MemoryStream();
            await partial.CopyToAsync(copy);
            Assert.Equal(raw[..20], copy.ToArray());
        }

        var request = new FetchRequest(MessageSummaryItems.UniqueId) { Headers = new HeaderSet(new[] { HeaderId.Subject, HeaderId.From }) };
        IMessageSummary headers = (await client.Inbox.FetchAsync(new[] { uid }, request)).Single();
        Assert.Equal("Bericht", headers.Headers![HeaderId.Subject]);
        Assert.Null(headers.Headers[HeaderId.To]);

        MimeMessage message = await client.Inbox.GetMessageAsync(uid);
        Assert.Equal("Bericht", message.Subject);
        Assert.Single(message.Attachments);

        // MailKit reads with BODY.PEEK, so the message is still unread.
        Assert.False((await ImapTestData.ReloadAsync(Host, stored.Id)).IsRead);
    }

    [DbFact]
    public async Task Body_sections_are_cut_exactly_from_the_message()
    {
        byte[] raw = ImapTestData.MultipartWithAttachment();
        string text = Encoding.UTF8.GetString(raw);
        MailMessage stored = await AddToInboxAsync(raw);
        await using RawImapClient client = await LoginRawAsync();
        await client.CommandAsync("s", "SELECT INBOX");

        string headerBlock = text[..(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)];
        List<string> header = await client.CommandAsync("a1", "UID FETCH 1 (BODY.PEEK[HEADER])");
        Assert.Equal($"* 1 FETCH (UID 1 BODY[HEADER] {{{Encoding.UTF8.GetByteCount(headerBlock)}}}\r\n{headerBlock})", header[0]);

        List<string> fields = await client.CommandAsync("a2", "UID FETCH 1 (BODY.PEEK[HEADER.FIELDS (Subject Message-ID)])");
        string expectedFields = "Subject: Bericht\r\nMessage-ID: <multi@sender.test>\r\n\r\n";
        Assert.Equal($"* 1 FETCH (UID 1 BODY[HEADER.FIELDS (SUBJECT MESSAGE-ID)] {{{expectedFields.Length}}}\r\n{expectedFields})", fields[0]);

        List<string> notFields = await client.CommandAsync("a3", "UID FETCH 1 (BODY.PEEK[HEADER.FIELDS.NOT (Subject From To Cc Date Message-ID MIME-Version)])");
        string expectedNot = "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n";
        Assert.Equal($"* 1 FETCH (UID 1 BODY[HEADER.FIELDS.NOT (SUBJECT FROM TO CC DATE MESSAGE-ID MIME-VERSION)] {{{expectedNot.Length}}}\r\n{expectedNot})", notFields[0]);

        List<string> part1 = await client.CommandAsync("a4", "FETCH 1 (BODY.PEEK[1])");
        string body1 = "Hallo Alice,\r\nanbei der Bericht über Köln.";
        Assert.Equal($"* 1 FETCH (BODY[1] {{{Encoding.UTF8.GetByteCount(body1)}}}\r\n{body1})", part1[0]);

        List<string> mime2 = await client.CommandAsync("a5", "FETCH 1 (BODY.PEEK[2.MIME])");
        string mime = "Content-Type: application/pdf; name=\"bericht.pdf\"\r\nContent-Disposition: attachment; filename=\"bericht.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\n";
        Assert.Equal($"* 1 FETCH (BODY[2.MIME] {{{mime.Length}}}\r\n{mime})", mime2[0]);

        Assert.Equal("* 1 FETCH (BODY[2] {12}\r\nJVBERi0xLjQK)", (await client.CommandAsync("a6", "FETCH 1 (BODY.PEEK[2])"))[0]);
        Assert.Equal("* 1 FETCH (BODY[]<6> {7}\r\n=?utf-8)", (await client.CommandAsync("a7", "FETCH 1 (BODY.PEEK[]<6.7>)"))[0]);
        Assert.Equal("* 1 FETCH (BODY[]<100000> {0}\r\n)", (await client.CommandAsync("a8", "FETCH 1 BODY.PEEK[]<100000.10>"))[0]);
        Assert.Equal("* 1 FETCH (BODY[3] NIL)", (await client.CommandAsync("a9", "FETCH 1 (BODY.PEEK[3])"))[0]);
        Assert.Equal($"* 1 FETCH (RFC822.SIZE {raw.Length})", (await client.CommandAsync("a10", "FETCH 1 RFC822.SIZE"))[0]);

        string textSection = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        List<string> bodyText = await client.CommandAsync("a11", "FETCH 1 (BODY.PEEK[TEXT])");
        Assert.Equal($"* 1 FETCH (BODY[TEXT] {{{Encoding.UTF8.GetByteCount(textSection)}}}\r\n{textSection})", bodyText[0]);

        // Without PEEK the message becomes \Seen, and the new flags come with the response.
        List<string> seen = await client.CommandAsync("a12", "FETCH 1 (BODY[2])");
        Assert.Equal("* 1 FETCH (BODY[2] {12}\r\nJVBERi0xLjQK FLAGS (\\Seen))", seen[0]);
        Assert.True((await ImapTestData.ReloadAsync(Host, stored.Id)).IsRead);
    }

    [DbFact]
    public async Task Messages_whose_content_is_not_available_answer_no_unavailable()
    {
        await AddToInboxAsync(ImapTestData.Simple("Stored here"));
        await AddToInboxAsync(ImapTestData.Simple("Only referenced"), m => m with { Storage = MessageStorage.Remote });

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");

        // What the database keeps is served without the provider...
        Assert.StartsWith("* 2 FETCH (UID 2 ENVELOPE (\"Tue, 07 Oct 2026 10:00:00 +0200\" \"Only referenced\"", (await raw.CommandAsync("a1", "UID FETCH 2 (UID ENVELOPE)"))[0]);
        Assert.Equal("* 2 FETCH (UID 2 BODY[HEADER.FIELDS (SUBJECT)] {28}\r\nSubject: Only referenced\r\n\r\n)", (await raw.CommandAsync("a2", "UID FETCH 2 (BODY.PEEK[HEADER.FIELDS (SUBJECT)])"))[0]);

        // ...the body needs the provider, which is not there.
        List<string> body = await raw.CommandAsync("a3", "UID FETCH 1:2 (BODY.PEEK[TEXT])");
        Assert.Equal("* 1 FETCH (UID 1 BODY[TEXT] {7}\r\nHello\r\n)", body[0]);
        Assert.Equal("* 2 FETCH (UID 2)", body[1]);
        Assert.Equal("a3 NO [UNAVAILABLE] Some message contents could not be loaded", body[^1]);

        // COPY is all or nothing: the copy of the first message is taken back.
        Assert.Equal("a4 NO [UNAVAILABLE] Some messages could not be read; nothing was copied", (await raw.CommandAsync("a4", "UID COPY 1:2 Archive"))[^1]);
        MailFolder archive = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive);
        Assert.Empty(await ImapTestData.MessagesAsync(Host, archive.Id));
    }

    [DbFact]
    public async Task Fetch_handles_many_messages_in_batches()
    {
        MailFolder inbox = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);
        using (IServiceScope scope = Host.Scope())
        {
            var store = scope.ServiceProvider.GetRequiredService<MailStore>();
            for (int i = 1; i <= 250; i++)
            {
                await store.AddAsync(inbox.Id, new NewMessage(ImapTestData.Simple($"Message {i:000}")) { IsRead = i % 2 == 0 });
            }
        }

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        IList<IMessageSummary> all = await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
        Assert.Equal(Enumerable.Range(1, 250).Select(i => $"Message {i:000}"), all.Select(s => s.Envelope!.Subject));
        Assert.Equal(125, all.Count(s => s.Flags!.Value.HasFlag(MessageFlags.Seen)));
        Assert.Equal(125, (await client.Inbox.SearchAsync(SearchQuery.NotSeen)).Count);
        Assert.Equal(250, (await client.Inbox.SearchAsync(SearchQuery.SubjectContains("Message"))).Count);
    }

    [DbFact]
    public async Task Uid_fetch_handles_ranges_gaps_and_the_star()
    {
        var messages = new List<MailMessage>();
        for (int i = 1; i <= 5; i++)
        {
            messages.Add(await AddToInboxAsync(ImapTestData.Simple($"Message {i}"), m => m with { IsDeleted = i == 3 }));
        }

        using (IServiceScope scope = Host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().ExpungeAsync(messages[0].FolderId);
        }

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(4, client.Inbox.Count);

        async Task<uint[]> UidsAsync(IList<UniqueId> uids) => (await client.Inbox.FetchAsync(uids, MessageSummaryItems.UniqueId)).Select(s => s.UniqueId.Id).ToArray();
        Assert.Equal(new uint[] { 2, 4 }, await UidsAsync(new UniqueIdRange(new UniqueId(2), new UniqueId(4))));
        Assert.Equal(new uint[] { 1, 2, 4, 5 }, await UidsAsync(UniqueIdRange.All));
        Assert.Equal(new uint[] { 5 }, await UidsAsync(new UniqueIdRange(new UniqueId(10), UniqueId.MaxValue)));
        Assert.Equal(new uint[] { 2, 4 }, (await client.Inbox.FetchAsync(1, 2, MessageSummaryItems.UniqueId)).Select(s => s.UniqueId.Id).ToArray());

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "EXAMINE INBOX");
        Assert.Equal(new[] { "* 4 FETCH (UID 5)", "a1 OK UID FETCH completed" }, await raw.CommandAsync("a1", "UID FETCH 10:* (UID)"));
        Assert.Equal(new[] { "a2 OK UID FETCH completed" }, await raw.CommandAsync("a2", "UID FETCH 3 (UID)"));
        Assert.Equal("a3 BAD Invalid message sequence number.", (await raw.CommandAsync("a3", "FETCH 7 (UID)"))[^1]);
        Assert.Equal(new[] { "* 2 FETCH (UID 2)", "* 3 FETCH (UID 4)", "a4 OK FETCH completed" }, await raw.CommandAsync("a4", "FETCH 3:2 (UID)"));
    }

}

/// <summary>STORE, EXPUNGE, SEARCH, APPEND, COPY and MOVE.</summary>
public class ImapMessageTests : ImapTestBase
{
    [DbFact]
    public async Task Store_sets_and_clears_flags_and_keywords()
    {
        MailMessage stored = await AddToInboxAsync(ImapTestData.Simple("Flags"));
        var uid = new UniqueId((uint)stored.Uid);

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
        await client.Inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered));
        MailMessage row = await ImapTestData.ReloadAsync(Host, stored.Id);
        Assert.True(row.IsRead && row.IsStarred && row.IsAnswered);

        await client.Inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Seen) { Silent = true });
        Assert.False((await ImapTestData.ReloadAsync(Host, stored.Id)).IsRead);

        var keywords = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None);
        keywords.Keywords.Add("$Label1");
        keywords.Keywords.Add("Wichtig");
        keywords.Keywords.Add("$Forwarded");
        await client.Inbox.StoreAsync(new[] { uid }, keywords);
        row = await ImapTestData.ReloadAsync(Host, stored.Id);
        Assert.Equal(new[] { "$Label1", "Wichtig" }, row.Keywords.Order(StringComparer.Ordinal));
        Assert.True(row.IsForwarded);

        IMessageSummary summary = (await client.Inbox.FetchAsync(new[] { uid }, MessageSummaryItems.Flags)).Single();
        Assert.Equal(MessageFlags.Flagged | MessageFlags.Answered, summary.Flags);
        Assert.Equal(new[] { "$Forwarded", "$Label1", "Wichtig" }, summary.Keywords.Order(StringComparer.Ordinal));

        await client.Inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Set, MessageFlags.Deleted) { Silent = true });
        row = await ImapTestData.ReloadAsync(Host, stored.Id);
        Assert.True(row.IsDeleted);
        Assert.False(row.IsStarred);
        Assert.Empty(row.Keywords);

        await client.Inbox.ExpungeAsync();
        Assert.Equal(0, client.Inbox.Count);
        Assert.Empty(await ImapTestData.MessagesAsync(Host, stored.FolderId));
    }

    [DbFact]
    public async Task Store_and_expunge_answer_with_the_documented_untagged_responses()
    {
        for (int i = 1; i <= 4; i++)
        {
            await AddToInboxAsync(ImapTestData.Simple($"Message {i}"));
        }

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        Assert.Equal(
            new[] { "* 2 FETCH (UID 2 FLAGS (\\Deleted))", "* 3 FETCH (UID 3 FLAGS (\\Deleted))", "a1 OK STORE completed" },
            await raw.CommandAsync("a1", "STORE 2:3 +FLAGS (\\Deleted)"));
        Assert.Equal(new[] { "a2 OK UID STORE completed" }, await raw.CommandAsync("a2", "UID STORE 4 +FLAGS.SILENT (\\Seen)"));
        Assert.Equal(
            new[]
            {
                "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded $Work)",
                "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft $Forwarded $Work \\*)] Flags permitted",
                "* 1 FETCH (UID 1 FLAGS (\\Flagged $Work))",
                "a3 OK STORE completed",
            },
            await raw.CommandAsync("a3", "STORE 1 FLAGS \\Flagged $Work"));
        Assert.Equal("a4 BAD Unknown system flag \\Bogus", (await raw.CommandAsync("a4", "STORE 1 +FLAGS (\\Bogus)"))[^1]);
        Assert.Equal(new[] { "* 3 EXPUNGE", "* 2 EXPUNGE", "a5 OK EXPUNGE completed" }, await raw.CommandAsync("a5", "EXPUNGE"));
        Assert.Equal(new[] { "* 2 FETCH (UID 4 FLAGS (\\Deleted \\Seen))", "a6 OK UID STORE completed" }, await raw.CommandAsync("a6", "UID STORE 4 +FLAGS (\\Deleted)"));
        Assert.Equal(new[] { "a7 OK UID EXPUNGE completed" }, await raw.CommandAsync("a7", "UID EXPUNGE 1"));
        Assert.Equal(new[] { "* 2 EXPUNGE", "a8 OK UID EXPUNGE completed" }, await raw.CommandAsync("a8", "UID EXPUNGE 1:*"));
        List<string> close = await raw.CommandAsync("a9", "CLOSE");
        Assert.Equal(new[] { "a9 OK CLOSE completed" }, close);
    }

    [DbFact]
    public async Task Search_supports_the_common_criteria()
    {
        MailMessage a = await AddToInboxAsync(
            ImapTestData.Simple("Angebot Küche", "Der Preis ist 4711 Euro", "Max Meier <max@sender.test>"),
            m => m with { IsRead = true, ReceivedDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc) });
        MailMessage b = await AddToInboxAsync(
            ImapTestData.Simple("Rechnung", "Bitte zahlen", "billing@shop.test"),
            m => m with { IsStarred = true, Keywords = new[] { "$Label1" }, ReceivedDate = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc) });
        MailMessage c = await AddToInboxAsync(
            ImapTestData.MultipartWithAttachment("Grüße aus Köln"),
            m => m with { ReceivedDate = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc) });

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        async Task<long[]> FindAsync(SearchQuery query) => (await client.Inbox.SearchAsync(query)).Select(u => (long)u.Id).ToArray();

        Assert.Equal(new[] { a.Uid }, await FindAsync(SearchQuery.SubjectContains("küche")));
        Assert.Equal(new[] { c.Uid }, await FindAsync(SearchQuery.SubjectContains("Grüße")));
        Assert.Equal(new[] { b.Uid }, await FindAsync(SearchQuery.FromContains("billing@shop")));
        Assert.Equal(new[] { a.Uid }, await FindAsync(SearchQuery.FromContains("Meier")));
        Assert.Equal(new[] { c.Uid }, await FindAsync(SearchQuery.CcContains("bob@")));
        Assert.Equal(new[] { a.Uid, b.Uid }, await FindAsync(SearchQuery.ToContains("alice@example.test").And(SearchQuery.Not(SearchQuery.CcContains("bob")))));
        Assert.Equal(new[] { a.Uid }, await FindAsync(SearchQuery.BodyContains("4711")));
        Assert.Equal(new[] { c.Uid }, await FindAsync(SearchQuery.MessageContains("köln")));
        Assert.Equal(new[] { a.Uid }, await FindAsync(SearchQuery.Seen));
        Assert.Equal(new[] { b.Uid, c.Uid }, await FindAsync(SearchQuery.NotSeen));
        Assert.Equal(new[] { b.Uid }, await FindAsync(SearchQuery.Flagged));
        Assert.Equal(new[] { a.Uid, c.Uid }, await FindAsync(SearchQuery.NotFlagged));
        Assert.Equal(new[] { b.Uid }, await FindAsync(SearchQuery.HasKeyword("$Label1")));
        Assert.Equal(new[] { a.Uid, c.Uid }, await FindAsync(SearchQuery.NotKeyword("$Label1")));
        Assert.Equal(new[] { b.Uid, c.Uid }, await FindAsync(SearchQuery.DeliveredAfter(new DateTime(2026, 9, 15))));
        Assert.Equal(new[] { a.Uid }, await FindAsync(SearchQuery.DeliveredBefore(new DateTime(2026, 9, 15))));
        Assert.Equal(new[] { b.Uid }, await FindAsync(SearchQuery.DeliveredOn(new DateTime(2026, 10, 1))));
        Assert.Equal(new[] { a.Uid, b.Uid, c.Uid }, await FindAsync(SearchQuery.SentOn(new DateTime(2026, 10, 7))));
        Assert.Empty(await FindAsync(SearchQuery.SentBefore(new DateTime(2026, 10, 7))));
        Assert.Equal(new[] { a.Uid, b.Uid, c.Uid }, await FindAsync(SearchQuery.SentSince(new DateTime(2026, 10, 7))));
        Assert.Equal(new[] { c.Uid }, await FindAsync(SearchQuery.LargerThan(600)));
        Assert.Equal(new[] { a.Uid, b.Uid }, await FindAsync(SearchQuery.SmallerThan(600)));
        Assert.Equal(new[] { a.Uid, c.Uid }, await FindAsync(SearchQuery.Or(SearchQuery.Seen, SearchQuery.SubjectContains("köln"))));
        Assert.Equal(new[] { c.Uid }, await FindAsync(SearchQuery.HeaderContains("Message-ID", "multi@sender")));
        Assert.Equal(new[] { b.Uid }, await FindAsync(SearchQuery.Uids(new[] { new UniqueId((uint)b.Uid) })));
        Assert.Equal(new[] { a.Uid, b.Uid, c.Uid }, await FindAsync(SearchQuery.All));
        Assert.Empty(await FindAsync(SearchQuery.Recent));
        Assert.Empty(await FindAsync(SearchQuery.Deleted));

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "EXAMINE INBOX");
        Assert.Equal(new[] { "* SEARCH 2 3", "a1 OK SEARCH completed" }, await raw.CommandAsync("a1", "SEARCH UNSEEN"));
        Assert.Equal(new[] { "* SEARCH 1 3", "a2 OK SEARCH completed" }, await raw.CommandAsync("a2", "SEARCH OR SEEN (SUBJECT gr OLD) NOT 2"));
        Assert.Equal(new[] { "* SEARCH 2", "a3 OK SEARCH completed" }, await raw.CommandAsync("a3", "SEARCH 2:* UNSEEN SMALLER 600"));
        Assert.Equal(new[] { "* SEARCH", "a4 OK UID SEARCH completed" }, await raw.CommandAsync("a4", "UID SEARCH NEW"));
        Assert.Equal("a5 NO [BADCHARSET (UTF-8 US-ASCII)] The charset is not supported", (await raw.CommandAsync("a5", "SEARCH CHARSET KOI8-R ALL"))[^1]);
        Assert.Equal("a6 BAD Unknown search criterion FUZZY.", (await raw.CommandAsync("a6", "SEARCH FUZZY x"))[^1]);
        await raw.SendAsync("a7 SEARCH CHARSET UTF-8 SUBJECT {7+}\r\n");
        await raw.SendAsync(Encoding.UTF8.GetBytes("Grüße\r\n"));
        Assert.Equal(new[] { "* SEARCH 3", "a7 OK SEARCH completed" }, await raw.ReadUntilTaggedAsync("a7"));
    }

    [DbFact]
    public async Task Append_stores_flags_and_internal_date_and_returns_the_uid()
    {
        using ImapClient client = await Imap.LoginAsync();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.test"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.test"));
        message.Subject = "Entwurf";
        message.Body = new TextPart("plain") { Text = "Noch nicht fertig" };
        var date = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.FromHours(2));

        IMailFolder drafts = client.GetFolder(SpecialFolder.Drafts)!;
        UniqueId? draftUid = await drafts.AppendAsync(new AppendRequest(message, MessageFlags.Draft | MessageFlags.Seen) { InternalDate = date });
        MailFolder draftsRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Drafts);
        MailMessage draft = Assert.Single(await ImapTestData.MessagesAsync(Host, draftsRow.Id));
        Assert.Equal(draft.Uid, draftUid!.Value.Id);
        Assert.Equal((uint)draftsRow.UidValidity, draftUid.Value.Validity);
        Assert.True(draft.IsDraft);
        Assert.True(draft.IsRead);
        Assert.Equal(date.UtcDateTime, draft.ReceivedDate);
        Assert.Equal("Entwurf", draft.Subject);

        IMailFolder sent = client.GetFolder(SpecialFolder.Sent)!;
        var sentRequest = new AppendRequest(message, MessageFlags.Seen);
        sentRequest.Keywords = new HashSet<string> { "$MDNSent" };
        UniqueId? sentUid = await sent.AppendAsync(sentRequest);
        MailFolder sentRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Sent);
        MailMessage sentMessage = Assert.Single(await ImapTestData.MessagesAsync(Host, sentRow.Id));
        Assert.Equal(sentMessage.Uid, sentUid!.Value.Id);
        Assert.Equal(new[] { "$MDNSent" }, sentMessage.Keywords);

        await using RawImapClient raw = await LoginRawAsync();
        await raw.SendAsync("a1 APPEND Nowhere {5+}\r\nHello\r\n");
        Assert.Equal("a1 NO [TRYCREATE] No such mailbox", (await raw.ReadUntilTaggedAsync("a1"))[^1]);
        await raw.SendAsync("a2 APPEND INBOX (\\Seen) \" 7-Oct-2026 23:15:00 -0700\" {5}\r\n");
        Assert.Equal("+ Ready for literal data", await raw.ReadLineAsync());
        await raw.SendAsync("Hello\r\n");
        Assert.StartsWith("a2 OK [APPENDUID ", (await raw.ReadUntilTaggedAsync("a2"))[^1]);
        MailFolder inbox = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);
        MailMessage appended = Assert.Single(await ImapTestData.MessagesAsync(Host, inbox.Id));
        Assert.Equal(new DateTime(2026, 10, 8, 6, 15, 0, DateTimeKind.Utc), appended.ReceivedDate);
        Assert.Equal("a3 BAD Invalid date-time.", (await raw.CommandAsync("a3", "APPEND INBOX \"yesterday\" {1+}\r\nx"))[^1]);
    }

    [DbFact]
    public async Task Large_messages_survive_append_and_fetch_byte_for_byte()
    {
        var attachment = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(attachment);
        var builder = new BodyBuilder { TextBody = "Anbei die Datei." };
        builder.Attachments.Add("daten.bin", attachment);
        var message = new MimeMessage { Subject = "Große Datei", Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("alice@example.test"));

        using ImapClient client = await Imap.LoginAsync();
        UniqueId? uid = await client.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.Seen));
        MailFolder inbox = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);
        MailMessage stored = Assert.Single(await ImapTestData.MessagesAsync(Host, inbox.Id));
        byte[] storedRaw = (await ImapTestData.ContentAsync(Host, stored.Id)).Raw!;

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        await using Stream stream = await client.Inbox.GetStreamAsync(uid!.Value, string.Empty);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(storedRaw, copy.ToArray());

        MimeMessage fetched = await client.Inbox.GetMessageAsync(uid.Value);
        using var content = new MemoryStream();
        await ((MimePart)fetched.Attachments.Single()).Content!.DecodeToAsync(content);
        Assert.Equal(attachment, content.ToArray());
    }

    [DbFact]
    public async Task Copy_returns_copyuid_and_keeps_the_originals()
    {
        MailMessage one = await AddToInboxAsync(ImapTestData.Simple("One"));
        await AddToInboxAsync(ImapTestData.Simple("Two"));
        MailMessage three = await AddToInboxAsync(ImapTestData.Simple("Three"), m => m with { IsStarred = true });

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
        IMailFolder archive = await client.GetFolderAsync("Archive");
        UniqueIdMap map = await client.Inbox.CopyToAsync(new[] { new UniqueId((uint)one.Uid), new UniqueId((uint)three.Uid) }, archive);

        Assert.Equal(new uint[] { 1, 3 }, map.Source.Select(u => u.Id));
        Assert.Equal(new uint[] { 1, 2 }, map.Destination.Select(u => u.Id));
        Assert.Equal(3, client.Inbox.Count);
        MailFolder archiveRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive);
        List<MailMessage> copies = await ImapTestData.MessagesAsync(Host, archiveRow.Id);
        Assert.Equal(new[] { "One", "Three" }, copies.Select(m => m.Subject));
        Assert.True(copies[1].IsStarred);

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        Assert.Equal($"a1 OK [COPYUID {archiveRow.UidValidity} 2 3] COPY completed", (await raw.CommandAsync("a1", "COPY 2 Archive"))[^1]);
        Assert.Equal("a2 NO [TRYCREATE] No such mailbox", (await raw.CommandAsync("a2", "COPY 1 Nowhere"))[^1]);
        Assert.Equal("a3 OK UID COPY completed", (await raw.CommandAsync("a3", "UID COPY 99 Archive"))[^1]);
    }

    [DbFact]
    public async Task Move_returns_copyuid_and_expunges_the_messages_from_the_source()
    {
        MailMessage one = await AddToInboxAsync(ImapTestData.Simple("One"));
        MailMessage two = await AddToInboxAsync(ImapTestData.Simple("Two"));
        await AddToInboxAsync(ImapTestData.Simple("Three"));

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
        var expunged = new List<int>();
        client.Inbox.MessageExpunged += (_, e) => expunged.Add(e.Index);
        IMailFolder archive = await client.GetFolderAsync("Archive");
        UniqueIdMap map = await client.Inbox.MoveToAsync(new[] { new UniqueId((uint)one.Uid), new UniqueId((uint)two.Uid) }, archive);

        Assert.Equal(new uint[] { 1, 2 }, map.Source.Select(u => u.Id));
        Assert.Equal(new uint[] { 1, 2 }, map.Destination.Select(u => u.Id));
        Assert.Equal(2, expunged.Count);
        Assert.Equal(1, client.Inbox.Count);
        MailFolder archiveRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Archive);
        Assert.Equal(new[] { "One", "Two" }, (await ImapTestData.MessagesAsync(Host, archiveRow.Id)).Select(m => m.Subject));

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT Archive");
        Assert.Equal("a1 NO [CANNOT] The messages are already in this mailbox", (await raw.CommandAsync("a1", "MOVE 1 Archive"))[^1]);
        long trashValidity = (await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Trash)).UidValidity;
        Assert.Equal(
            new[] { $"* OK [COPYUID {trashValidity} 1:2 1:2] Moved", "* 2 EXPUNGE", "* 1 EXPUNGE", "a2 OK UID MOVE completed" },
            await raw.CommandAsync("a2", "UID MOVE 1:* Trash"));
        Assert.Equal("a3 NO [TRYCREATE] No such mailbox", (await raw.CommandAsync("a3", "UID MOVE 1:* Nowhere"))[^1]);
    }

}

/// <summary>Changes made elsewhere (delivery, the web client, other sessions) reach the client: IDLE, NOOP, UIDNEXT.</summary>
public class ImapChangeNotificationTests : ImapTestBase
{
    [DbFact]
    public async Task Idle_reports_mail_delivered_by_the_delivery_service()
    {
        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var arrived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Inbox.CountChanged += (_, _) => arrived.TrySetResult(client.Inbox.Count);

        using var done = new CancellationTokenSource();
        Task idle = client.IdleAsync(done.Token);
        await Task.Delay(100);
        await ImapTestData.DeliverAsync(Host, "alice@example.test", "While you were idling");

        int count = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await done.CancelAsync();
        await idle;
        Assert.Equal(1, count);

        IMessageSummary summary = (await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.Envelope)).Single();
        Assert.Equal("While you were idling", summary.Envelope!.Subject);
    }

    [DbFact]
    public async Task Idle_pushes_flag_changes_and_expunges_made_elsewhere()
    {
        MailMessage first = await AddToInboxAsync(ImapTestData.Simple("First"));
        MailMessage second = await AddToInboxAsync(ImapTestData.Simple("Second"));

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        await raw.SendAsync("i1 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());

        using (IServiceScope scope = Host.ScopeAs(Seed.Alice))
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().ChangeFlagsAsync(new[] { second.Id }, new FlagChange { IsStarred = true });
        }

        Assert.Equal($"* 2 FETCH (UID {second.Uid} FLAGS (\\Flagged))", await raw.ReadLineAsync());

        using (IServiceScope scope = Host.ScopeAs(Seed.Alice))
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().DeleteAsync(new[] { first.Id });
        }

        Assert.Equal("* 1 EXPUNGE", await raw.ReadLineAsync());
        await raw.SendAsync("DONE\r\n");
        Assert.Equal("i1 OK IDLE terminated", await raw.ReadLineAsync());
    }

    [DbFact]
    public async Task Idle_notices_changes_without_a_notification_through_the_poll()
    {
        MailMessage message = await AddToInboxAsync(ImapTestData.Simple("Polled"));
        Imap.Server.IdlePollInterval = TimeSpan.FromMilliseconds(200);
        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        await raw.SendAsync("i1 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());

        // A change the event hub does not hear about (another server instance, a repair in the database).
        using (IServiceScope scope = Host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MailMessage\" SET \"IsStarred\" = true WHERE \"Id\" = {message.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MailFolder\" SET \"ModSeq\" = \"ModSeq\" + 1 WHERE \"Id\" = {message.FolderId}");
        }

        Assert.Equal("* 1 FETCH (UID 1 FLAGS (\\Flagged))", await raw.ReadLineAsync());
        await raw.SendAsync("DONE\r\n");
        Assert.Equal("i1 OK IDLE terminated", await raw.ReadLineAsync());
    }

    [DbFact]
    public async Task A_folder_deleted_elsewhere_ends_the_sessions_that_have_it_selected()
    {
        MailFolder projects = await ImapTestData.CreateFolderAsync(Host, AliceId, "Projekte");
        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT Projekte");

        using (IServiceScope scope = Host.ScopeAs(Seed.Alice))
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<FolderService>().DeleteAsync(projects.Id));
        }

        await raw.SendAsync("a1 NOOP\r\n");
        Assert.Equal("* BYE The selected mailbox no longer exists", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task Two_sessions_see_each_others_changes()
    {
        await AddToInboxAsync(ImapTestData.Simple("One"));
        await AddToInboxAsync(ImapTestData.Simple("Two"));

        using ImapClient first = await Imap.LoginAsync();
        using ImapClient second = await Imap.LoginAsync();
        await first.Inbox.OpenAsync(FolderAccess.ReadWrite);
        await second.Inbox.OpenAsync(FolderAccess.ReadWrite);

        var flagChanges = new List<(int Index, MessageFlags Flags)>();
        second.Inbox.MessageFlagsChanged += (_, e) => flagChanges.Add((e.Index, e.Flags));
        await first.Inbox.StoreAsync(new[] { 0 }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Flagged) { Silent = true });
        await second.NoOpAsync();
        Assert.Equal(new[] { (0, MessageFlags.Flagged) }, flagChanges);

        var expunged = new List<int>();
        second.Inbox.MessageExpunged += (_, e) => expunged.Add(e.Index);
        await first.Inbox.StoreAsync(new[] { 1 }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
        await first.Inbox.ExpungeAsync();
        await second.NoOpAsync();
        Assert.Equal(new[] { 1 }, expunged);
        Assert.Equal(1, second.Inbox.Count);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("max@sender.test"));
        message.Subject = "Appended";
        message.Body = new TextPart("plain") { Text = "x" };
        await first.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None));
        await second.NoOpAsync();
        Assert.Equal(2, second.Inbox.Count);
    }

    [DbFact]
    public async Task Parallel_sessions_append_and_flag_without_losing_anything()
    {
        const int Sessions = 5;
        const int MessagesPerSession = 6;
        var clients = new List<ImapClient>();
        try
        {
            for (int i = 0; i < Sessions; i++)
            {
                ImapClient client = await Imap.LoginAsync();
                await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
                clients.Add(client);
            }

            await Task.WhenAll(clients.Select(async (client, session) =>
            {
                for (int i = 0; i < MessagesPerSession; i++)
                {
                    var message = new MimeMessage { Subject = $"Session {session} message {i}" };
                    message.From.Add(MailboxAddress.Parse("alice@example.test"));
                    message.Body = new TextPart("plain") { Text = "x" };
                    UniqueId? uid = await client.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None));
                    await client.Inbox.StoreAsync(new[] { uid!.Value }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Flagged) { Silent = true });
                }
            }));

            foreach (ImapClient client in clients)
            {
                await client.NoOpAsync();
                Assert.Equal(Sessions * MessagesPerSession, client.Inbox.Count);
                IList<IMessageSummary> summaries = await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
                Assert.Equal(Enumerable.Range(1, Sessions * MessagesPerSession).Select(i => (uint)i), summaries.Select(s => s.UniqueId.Id));
                Assert.All(summaries, s => Assert.True(s.Flags!.Value.HasFlag(MessageFlags.Flagged)));
            }
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [DbFact]
    public async Task Mailkit_previews_references_and_full_headers_work()
    {
        await AddToInboxAsync(ImapTestData.MultipartWithAttachment());
        await AddToInboxAsync(ImapTestData.Simple("Re: Bericht", "Danke!", extraHeaders: "In-Reply-To: <multi@sender.test>\r\nReferences: <multi@sender.test>\r\n"));

        using ImapClient client = await Imap.LoginAsync();
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        IList<IMessageSummary> summaries = await client.Inbox.FetchAsync(0, -1,
            MessageSummaryItems.PreviewText | MessageSummaryItems.References | MessageSummaryItems.Headers | MessageSummaryItems.UniqueId);

        Assert.StartsWith("Hallo Alice,", summaries[0].PreviewText);
        Assert.Equal("Danke!", summaries[1].PreviewText?.Trim());
        Assert.Equal("multi@sender.test", summaries[1].References!.Single());
        Assert.Equal("Bericht", summaries[0].Headers![HeaderId.Subject]);
    }

    [DbFact]
    public async Task Expunges_are_not_reported_during_fetch_store_and_search()
    {
        MailMessage first = await AddToInboxAsync(ImapTestData.Simple("First"));
        await AddToInboxAsync(ImapTestData.Simple("Second"));

        await using RawImapClient raw = await LoginRawAsync();
        await raw.CommandAsync("s", "SELECT INBOX");
        using (IServiceScope scope = Host.ScopeAs(Seed.Alice))
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().DeleteAsync(new[] { first.Id }, permanent: true);
        }

        // The first FETCH still answers from what the session knows; afterwards the session knows the message is gone, but it may
        // not say so during FETCH, STORE or SEARCH: the client keeps counting it and FETCH skips it.
        Assert.Equal(new[] { "* 1 FETCH (UID 1)", "* 2 FETCH (UID 2)", "a1 OK FETCH completed" }, await raw.CommandAsync("a1", "FETCH 1:2 (UID)"));
        Assert.Equal(new[] { "* 2 FETCH (UID 2)", "a2 OK [EXPUNGEISSUED] Some messages were expunged; FETCH completed" }, await raw.CommandAsync("a2", "FETCH 1:2 (UID)"));
        Assert.Equal(new[] { "* SEARCH 2", "a3 OK SEARCH completed" }, await raw.CommandAsync("a3", "SEARCH ALL"));
        Assert.Equal(new[] { "* 2 FETCH (UID 2 FLAGS (\\Seen))", "a4 OK [EXPUNGEISSUED] Some messages were expunged; STORE completed" }, await raw.CommandAsync("a4", "STORE 1:2 +FLAGS (\\Seen)"));
        Assert.Equal(new[] { "* 1 EXPUNGE", "a5 OK NOOP completed" }, await raw.CommandAsync("a5", "NOOP"));
        Assert.Equal(new[] { "* 1 FETCH (UID 2)", "a6 OK FETCH completed" }, await raw.CommandAsync("a6", "FETCH 1:* (UID)"));
    }

    [DbFact]
    public async Task Uidvalidity_stays_and_uidnext_grows_with_every_new_message()
    {
        MailFolder inboxRow = await ImapTestData.FolderAsync(Host, AliceId, FolderKind.Inbox);
        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal(new[] { "* STATUS \"INBOX\" (UIDNEXT 1 UIDVALIDITY " + inboxRow.UidValidity + ")", "a1 OK STATUS completed" }, await raw.CommandAsync("a1", "STATUS INBOX (UIDNEXT UIDVALIDITY)"));

        Assert.Equal($"a2 OK [APPENDUID {inboxRow.UidValidity} 1] APPEND completed", (await raw.CommandAsync("a2", "APPEND INBOX {5+}\r\nHello"))[^1]);
        Assert.Equal($"a3 OK [APPENDUID {inboxRow.UidValidity} 2] APPEND completed", (await raw.CommandAsync("a3", "APPEND INBOX {5+}\r\nWorld"))[^1]);
        await raw.CommandAsync("a4", "SELECT INBOX");
        await raw.CommandAsync("a5", "STORE 1:2 +FLAGS.SILENT (\\Deleted)");
        await raw.CommandAsync("a6", "EXPUNGE");

        // UIDs are never handed out twice.
        Assert.Equal($"a7 OK [APPENDUID {inboxRow.UidValidity} 3] APPEND completed", (await raw.CommandAsync("a7", "APPEND INBOX {5+}\r\nAgain"))[^1]);
        List<string> select = await raw.CommandAsync("a8", "SELECT INBOX");
        Assert.Contains($"* OK [UIDVALIDITY {inboxRow.UidValidity}] UIDs valid", select);
        Assert.Contains("* OK [UIDNEXT 4] Predicted next UID", select);
        Assert.Contains("* 1 EXISTS", select);
    }

}

/// <summary>Other people's and shared mailboxes below "Shared/": access levels Read, Send, none, and "Unassigned" for administrators.</summary>
public class ImapSharedMailboxTests : ImapTestBase
{
    [DbFact]
    public async Task A_shared_mailbox_with_read_access_can_only_be_read()
    {
        MailMessage request = await ImapTestData.AddAsync(Host, Seed.Info.Id, FolderKind.Inbox, ImapTestData.Simple("Anfrage an info@"));
        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Alice, MailboxAccess.Read);

        using ImapClient client = await Imap.LoginAsync();
        IList<IMailFolder> shared = await client.GetFoldersAsync(client.SharedNamespaces[0]);
        Assert.Contains(shared, f => f.FullName == "Shared/Info/INBOX");
        Assert.Contains(shared, f => f.FullName == "Shared/Info/Sent");

        IMailFolder infoInbox = await client.GetFolderAsync("Shared/Info/INBOX");
        Assert.Equal(FolderAccess.ReadOnly, await infoInbox.OpenAsync(FolderAccess.ReadWrite));
        Assert.Equal("Anfrage an info@", (await infoInbox.GetMessageAsync(0)).Subject);

        UniqueIdMap copied = await infoInbox.CopyToAsync(new[] { new UniqueId((uint)request.Uid) }, await client.GetFolderAsync("Archive"));
        Assert.Single(copied.Destination);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.Body = new TextPart("plain") { Text = "x" };
        await Assert.ThrowsAsync<ImapCommandException>(() => infoInbox.AppendAsync(new AppendRequest(message, MessageFlags.None)));

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal("a1 OK [READ-ONLY] SELECT completed", (await raw.CommandAsync("a1", "SELECT Shared/Info/INBOX"))[^1]);
        Assert.Equal("a2 NO [NOPERM] You may only read this mailbox", (await raw.CommandAsync("a2", "STORE 1 +FLAGS (\\Seen)"))[^1]);
        Assert.Equal("a3 NO [NOPERM] You may only read this mailbox", (await raw.CommandAsync("a3", "EXPUNGE"))[^1]);
        Assert.Equal("a4 NO [NOPERM] You may only read this mailbox", (await raw.CommandAsync("a4", "MOVE 1 Archive"))[^1]);
        Assert.Equal("a5 NO [NOPERM] You may not create folders in this mailbox", (await raw.CommandAsync("a5", "CREATE Shared/Info/Neu"))[^1]);
        Assert.Equal("a6 NO [NOPERM] You may not add messages to this mailbox", (await raw.CommandAsync("a6", "COPY 1 Shared/Info/Sent"))[^1]);
        List<string> body = await raw.CommandAsync("a7", "FETCH 1 (BODY[TEXT])");
        Assert.DoesNotContain("FLAGS", body[0]);
        Assert.False((await ImapTestData.ReloadAsync(Host, request.Id)).IsRead);
    }

    [DbFact]
    public async Task A_shared_mailbox_with_send_access_can_be_worked_in_but_not_restructured()
    {
        MailMessage request = await ImapTestData.AddAsync(Host, Seed.Info.Id, FolderKind.Inbox, ImapTestData.Simple("Bitte um Rückruf"));
        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Alice, MailboxAccess.Send);

        using ImapClient client = await Imap.LoginAsync();
        IMailFolder infoInbox = await client.GetFolderAsync("Shared/Info/INBOX");
        Assert.Equal(FolderAccess.ReadWrite, await infoInbox.OpenAsync(FolderAccess.ReadWrite));
        await infoInbox.StoreAsync(new[] { 0 }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen | MessageFlags.Answered));
        MailMessage row = await ImapTestData.ReloadAsync(Host, request.Id);
        Assert.True(row.IsRead && row.IsAnswered);

        var reply = new MimeMessage();
        reply.From.Add(MailboxAddress.Parse("info@example.test"));
        reply.Subject = "Re: Bitte um Rückruf";
        reply.Body = new TextPart("plain") { Text = "Gerne." };
        Assert.NotNull(await (await client.GetFolderAsync("Shared/Info/Sent")).AppendAsync(new AppendRequest(reply, MessageFlags.Seen)));

        UniqueIdMap moved = await infoInbox.MoveToAsync(new[] { new UniqueId((uint)request.Uid) }, await client.GetFolderAsync("Shared/Info/Archive"));
        Assert.Single(moved.Destination);
        Assert.Equal(0, infoInbox.Count);

        IMailFolder infoRoot = await client.GetFolderAsync("Shared/Info");
        await Assert.ThrowsAsync<ImapCommandException>(() => infoRoot.CreateAsync("Projekte", true));
    }

    [DbFact]
    public async Task Mailboxes_without_access_stay_invisible()
    {
        await ImapTestData.AddAsync(Host, Seed.BobMailbox.Id, FolderKind.Inbox, ImapTestData.Simple("Private to Bob"));

        using ImapClient client = await Imap.LoginAsync();
        IList<IMailFolder> all = await client.GetFoldersAsync(client.PersonalNamespaces[0]);
        Assert.DoesNotContain(all, f => f.FullName.StartsWith("Shared", StringComparison.Ordinal));
        await Assert.ThrowsAsync<FolderNotFoundException>(() => client.GetFolderAsync("Shared/Bob/INBOX"));

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal("a1 NO [NONEXISTENT] No such mailbox", (await raw.CommandAsync("a1", "SELECT Shared/Bob/INBOX"))[^1]);
        Assert.Equal(new[] { "a2 OK LIST completed" }, await raw.CommandAsync("a2", "LIST \"\" \"Shared/*\""));
        Assert.Equal("a3 NO [CANNOT] Folders can only be created inside a shared mailbox (Shared/<mailbox>/<folder>)", (await raw.CommandAsync("a3", "CREATE Shared/Bob/Spy"))[^1]);
    }

    [DbFact]
    public async Task Mailboxes_with_the_same_name_get_distinct_labels_and_managers_may_restructure_them()
    {
        Mailbox second;
        using (IServiceScope scope = Host.Scope())
        {
            second = await scope.ServiceProvider.GetRequiredService<Services.MailboxService>().CreateMailboxAsync("Info", MailboxType.Shared, null, Seed.Tenant.Id);
        }

        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Alice, MailboxAccess.Read);
        await ImapTestData.GrantAsync(Host, second, Seed.Alice, MailboxAccess.Manage);

        await using RawImapClient raw = await LoginRawAsync();
        Assert.Equal(
            new[]
            {
                "* LIST (\\Noselect \\HasChildren) \"/\" \"Shared/Info\"",
                "* LIST (\\Noselect \\HasChildren) \"/\" \"Shared/Info (2)\"",
                "a1 OK LIST completed",
            },
            await raw.CommandAsync("a1", "LIST \"\" \"Shared/%\""));
        Assert.Equal(new[] { "* LIST (\\Noselect \\HasChildren) \"/\" \"Shared\"", "a2 OK LIST completed" }, await raw.CommandAsync("a2", "LIST \"\" Shared"));

        Assert.Equal("a3 OK CREATE completed", (await raw.CommandAsync("a3", "CREATE \"Shared/Info (2)/Projekte\""))[^1]);
        Assert.NotNull(await ImapTestData.FolderAsync(Host, second.Id, "Projekte"));
        Assert.Equal("a4 OK RENAME completed", (await raw.CommandAsync("a4", "RENAME \"Shared/Info (2)/Projekte\" \"Shared/Info (2)/Kunden\""))[^1]);
        Assert.Equal("a5 NO [CANNOT] Folders can only be renamed within their mailbox", (await raw.CommandAsync("a5", "RENAME \"Shared/Info (2)/Kunden\" Kunden"))[^1]);
        Assert.Equal("a6 OK DELETE completed", (await raw.CommandAsync("a6", "DELETE \"Shared/Info (2)/Kunden\""))[^1]);
        Assert.Equal("a7 NO [NOPERM] Subscriptions of this shared mailbox are managed by its owner", (await raw.CommandAsync("a7", "UNSUBSCRIBE Shared/Info/INBOX"))[^1]);
        Assert.Equal("a8 OK UNSUBSCRIBE completed", (await raw.CommandAsync("a8", "UNSUBSCRIBE \"Shared/Info (2)/Junk\""))[^1]);
        Assert.Equal("a9 NO [CANNOT] This name cannot be selected", (await raw.CommandAsync("a9", "SELECT Shared/Info"))[^1]);
    }

    [DbFact]
    public async Task Administrators_see_the_unassigned_mailbox()
    {
        await ImapTestData.DeliverAsync(Host, "nobody@example.test", "Lost and found");
        await ImapTestData.MakeAdministratorAsync(Host, Seed.Alice);

        using ImapClient client = await Imap.LoginAsync();
        IMailFolder unassigned = await client.GetFolderAsync("Shared/Unassigned/INBOX");
        Assert.Equal(FolderAccess.ReadWrite, await unassigned.OpenAsync(FolderAccess.ReadWrite));
        Assert.Equal("Lost and found", (await unassigned.GetMessageAsync(0)).Subject);

        // Bob is a plain user: no Unassigned mailbox for him.
        using ImapClient bob = await Imap.LoginAsync("bob");
        await Assert.ThrowsAsync<FolderNotFoundException>(() => bob.GetFolderAsync("Shared/Unassigned/INBOX"));
    }

}

/// <summary>The command sequences Outlook, Thunderbird and Apple Mail send right after connecting.</summary>
public class ImapClientSequenceTests : ImapTestBase
{
    [DbFact]
    public async Task Outlook_style_connect_sequence()
    {
        await AddToInboxAsync(ImapTestData.Simple("Für Outlook"), m => m with { IsRead = true });
        await AddToInboxAsync(ImapTestData.MultipartWithAttachment());
        await using RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();

        Assert.StartsWith("o1 OK", (await raw.CommandAsync("o1", "CAPABILITY"))[^1]);
        Assert.StartsWith("o2 OK", (await raw.CommandAsync("o2", $"LOGIN \"alice\" \"{ImapTestServer.Password}\""))[^1]);
        List<string> list = await raw.CommandAsync("o3", "LIST \"\" \"*\"");
        Assert.Equal(7, list.Count);
        List<string> lsub = await raw.CommandAsync("o4", "LSUB \"\" \"*\"");
        Assert.Equal(7, lsub.Count);
        foreach (string folder in new[] { "INBOX", "Drafts", "Sent", "Archive", "Junk", "Trash" })
        {
            List<string> status = await raw.CommandAsync("o5", $"STATUS \"{folder}\" (MESSAGES UNSEEN UIDNEXT UIDVALIDITY)");
            Assert.StartsWith($"* STATUS \"{folder}\" (MESSAGES ", status[0]);
            Assert.Equal("o5 OK STATUS completed", status[^1]);
        }

        Assert.Equal("o6 OK [READ-WRITE] SELECT completed", (await raw.CommandAsync("o6", "SELECT \"INBOX\""))[^1]);
        Assert.Equal(new[] { "* 1 FETCH (UID 1 FLAGS (\\Seen))", "* 2 FETCH (UID 2 FLAGS ())", "o7 OK UID FETCH completed" }, await raw.CommandAsync("o7", "UID FETCH 1:* (UID FLAGS)"));
        List<string> headers = await raw.CommandAsync("o8", "UID FETCH 1:2 (UID RFC822.SIZE FLAGS BODY.PEEK[HEADER])");
        Assert.Equal(3, headers.Count);
        Assert.StartsWith("* 1 FETCH (UID 1 RFC822.SIZE ", headers[0]);
        Assert.Contains("Subject: Für Outlook", headers[0]);
        List<string> full = await raw.CommandAsync("o9", "UID FETCH 2 (UID RFC822.SIZE BODY.PEEK[])");
        Assert.Contains("JVBERi0xLjQK", full[0]);
        Assert.Equal(new[] { "* BYE MatMail IMAP server logging out", "o10 OK LOGOUT completed" }, await raw.CommandAsync("o10", "LOGOUT"));
    }

    [DbFact]
    public async Task Thunderbird_style_connect_sequence()
    {
        await AddToInboxAsync(ImapTestData.Simple("Für Thunderbird"));
        await using RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();

        Assert.StartsWith("1 OK", (await raw.CommandAsync("1", "capability"))[^1]);
        Assert.StartsWith("2 OK", (await raw.CommandAsync("2", "authenticate PLAIN " + PlainResponse("alice", ImapTestServer.Password)))[^1]);
        List<string> id = await raw.CommandAsync("3", "ID (\"name\" \"Thunderbird\" \"version\" \"128.3.1\")");
        Assert.StartsWith("* ID (\"name\" \"MatMail\"", id[0]);
        Assert.Equal("* NAMESPACE ((\"\" \"/\")) NIL ((\"Shared/\" \"/\"))", (await raw.CommandAsync("4", "namespace"))[0]);
        List<string> folders = await raw.CommandAsync("5", "list (subscribed) \"\" \"*\" return (special-use)");
        Assert.Contains("* LIST (\\HasNoChildren \\Drafts \\Subscribed) \"/\" \"Drafts\"", folders);
        Assert.StartsWith("6 OK [READ-WRITE]", (await raw.CommandAsync("6", "select \"INBOX\""))[^1]);
        Assert.Equal(new[] { "* 1 FETCH (UID 1 FLAGS ())", "7 OK UID FETCH completed" }, await raw.CommandAsync("7", "UID fetch 1:* (FLAGS)"));
        List<string> headers = await raw.CommandAsync("8", "UID fetch 1 (UID RFC822.SIZE FLAGS BODY.PEEK[HEADER.FIELDS (From To Cc Bcc Subject Date Message-ID Priority X-Priority References Newsgroups In-Reply-To Content-Type Reply-To)])");
        Assert.StartsWith("* 1 FETCH (UID 1 RFC822.SIZE ", headers[0]);
        Assert.Contains("BODY[HEADER.FIELDS (FROM TO CC BCC SUBJECT DATE MESSAGE-ID PRIORITY X-PRIORITY REFERENCES NEWSGROUPS IN-REPLY-TO CONTENT-TYPE REPLY-TO)]", headers[0]);
        Assert.Contains("Subject: Für Thunderbird\r\n", headers[0], StringComparison.Ordinal);
        Assert.DoesNotContain("MIME-Version", headers[0]);

        await raw.SendAsync("9 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());
        await ImapTestData.DeliverAsync(Host, "alice@example.test", "New while idling");
        Assert.Equal("* 2 EXISTS", await raw.ReadLineAsync());
        await raw.SendAsync("DONE\r\n");
        Assert.Equal("9 OK IDLE terminated", await raw.ReadLineAsync());
        Assert.Equal("10 OK LOGOUT completed", (await raw.CommandAsync("10", "logout"))[^1]);
    }

    [DbFact]
    public async Task Apple_mail_style_connect_sequence()
    {
        MailMessage stored = await AddToInboxAsync(ImapTestData.MultipartWithAttachment());
        await using RawImapClient raw = await Imap.RawAsync(implicitTls: true);
        await raw.ReadLineAsync();

        Assert.StartsWith("1 OK", (await raw.CommandAsync("1", "ID (\"name\" \"iPhone Mail\" \"version\" \"22A3354\" \"os\" \"iOS\")"))[^1]);
        Assert.StartsWith("2 OK", (await raw.CommandAsync("2", $"LOGIN alice \"{ImapTestServer.Password}\""))[^1]);
        Assert.Equal(new[] { "* LIST (\\Noselect) \"/\" \"\"", "3 OK LIST completed" }, await raw.CommandAsync("3", "LIST \"\" \"\""));
        Assert.Equal(7, (await raw.CommandAsync("4", "LIST \"\" \"*\"")).Count);
        Assert.StartsWith("5 OK", (await raw.CommandAsync("5", "SELECT INBOX"))[^1]);
        Assert.Equal(new[] { "* 1 FETCH (UID 1 FLAGS ())", "6 OK UID FETCH completed" }, await raw.CommandAsync("6", "UID FETCH 1:* (FLAGS)"));
        List<string> structure = await raw.CommandAsync("7", $"UID FETCH {stored.Uid} (BODYSTRUCTURE)");
        Assert.StartsWith("* 1 FETCH (UID 1 BODYSTRUCTURE ((\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"8BIT\" ", structure[0]);
        Assert.Contains("(\"application\" \"pdf\" (\"name\" \"bericht.pdf\") NIL NIL \"BASE64\" 12 NIL (\"attachment\" (\"filename\" \"bericht.pdf\")) NIL NIL)", structure[0]);
        Assert.EndsWith(" \"mixed\" (\"boundary\" \"outer\") NIL NIL NIL))", structure[0]);
        List<string> text = await raw.CommandAsync("8", $"UID FETCH {stored.Uid} (BODY.PEEK[1]<0.10>)");
        Assert.Equal("* 1 FETCH (UID 1 BODY[1]<0> {10}\r\nHallo Alic)", text[0]);

        await raw.SendAsync("9 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());
        await raw.SendAsync("DONE\r\n");
        Assert.Equal("9 OK IDLE terminated", await raw.ReadLineAsync());
    }
}

/// <summary>
/// Raw protocol tests against a server that allows signing in without TLS and has no certificate, so plain sockets can pin the
/// exact syntax: tagged responses, literals, pipelining, response formats.
/// </summary>
public class ImapProtocolTests : ImapTestBase
{
    private const string Capabilities = "IMAP4rev1 LITERAL+ SASL-IR ID ENABLE IDLE NAMESPACE UNSELECT UIDPLUS MOVE CHILDREN SPECIAL-USE LIST-EXTENDED LIST-STATUS APPENDLIMIT=104857600";

    protected override bool WithCertificate => false;

    protected override void Configure(Configuration.AppConfig config) => config.Imap.RequireTls = false;

    private async Task<RawImapClient> ConnectAsync(bool login = false)
    {
        RawImapClient raw = await Imap.RawAsync();
        await raw.ReadLineAsync();
        if (login)
        {
            Assert.StartsWith("L0 OK", (await raw.CommandAsync("L0", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
        }

        return raw;
    }

    [DbFact]
    public async Task Without_a_certificate_there_is_no_tls_and_plain_login_is_offered()
    {
        await using RawImapClient raw = await Imap.RawAsync();
        Assert.Equal($"* OK [CAPABILITY {Capabilities} AUTH=PLAIN] mail.example.test MatMail IMAP4rev1 server ready", await raw.ReadLineAsync());
        Assert.Null(Imap.Server.TlsEndpoint);
        Assert.Equal(new[] { $"* CAPABILITY {Capabilities} AUTH=PLAIN", "a1 OK CAPABILITY completed" }, await raw.CommandAsync("a1", "CAPABILITY"));
        Assert.Equal(new[] { "a2 BAD STARTTLS is not available" }, await raw.CommandAsync("a2", "STARTTLS"));
        Assert.Equal(new[] { $"a3 OK [CAPABILITY {Capabilities}] Logged in" }, await raw.CommandAsync("a3", $"LOGIN alice {ImapTestServer.Password}"));
        Assert.Equal(new[] { $"* CAPABILITY {Capabilities}", "a4 OK CAPABILITY completed" }, await raw.CommandAsync("a4", "CAPABILITY"));
    }

    [DbFact]
    public async Task Tagged_responses_have_the_documented_formats()
    {
        await using RawImapClient raw = await ConnectAsync();
        Assert.Equal(new[] { "a1 OK NOOP completed" }, await raw.CommandAsync("a1", "NOOP"));
        Assert.Equal(new[] { "a2 BAD Unknown command FROBNICATE" }, await raw.CommandAsync("a2", "FROBNICATE"));
        Assert.Equal(new[] { "a3 BAD Please log in first" }, await raw.CommandAsync("a3", "SELECT INBOX"));
        Assert.Equal(new[] { "a4 NO [AUTHENTICATIONFAILED] Invalid credentials" }, await raw.CommandAsync("a4", "LOGIN alice wrong-password"));
        Assert.Equal(new[] { "a5 BAD Expected a space." }, await raw.CommandAsync("a5", "LOGIN alice"));
        Assert.Equal(new[] { "a6 BAD Unknown UID command" }, await raw.CommandAsync("a6", "UID FROB 1"));
        Assert.Equal(new[] { "a7 BAD Unexpected text at the end of the command." }, await raw.CommandAsync("a7", "NOOP now"));
        Assert.Equal(new[] { "a8 BAD Unterminated quoted string." }, await raw.CommandAsync("a8", "LOGIN \"alice"));

        await raw.SendAsync("(garbage)\r\n");
        Assert.Equal("* BAD Invalid command line", await raw.ReadLineAsync());
        await raw.SendAsync("}}} garbage\r\n");
        Assert.Equal("}}} BAD Unknown command GARBAGE", await raw.ReadLineAsync());
        await raw.SendAsync("\r\n");
        Assert.Equal("* BAD Invalid command line", await raw.ReadLineAsync());
        await raw.SendAsync("a9\r\n");
        Assert.Equal("a9 BAD Missing command name", await raw.ReadLineAsync());

        // After all that the session still works.
        Assert.StartsWith("b1 OK", (await raw.CommandAsync("b1", $"LOGIN alice {ImapTestServer.Password}"))[^1]);
        Assert.Equal(new[] { "b2 NO [NONEXISTENT] No such mailbox" }, await raw.CommandAsync("b2", "SELECT Nowhere"));
        Assert.Equal(new[] { "b3 BAD No mailbox selected" }, await raw.CommandAsync("b3", "EXPUNGE"));
        Assert.Equal(new[] { "b4 BAD Already logged in" }, await raw.CommandAsync("b4", "LOGIN alice x"));
        Assert.Equal(new[] { "* BYE MatMail IMAP server logging out", "b5 OK LOGOUT completed" }, await raw.CommandAsync("b5", "LOGOUT"));
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task Synchronizing_literals_get_a_continuation_and_literal_plus_does_not()
    {
        await using RawImapClient raw = await ConnectAsync();
        await raw.SendAsync("a1 LOGIN {5}\r\n");
        Assert.Equal("+ Ready for literal data", await raw.ReadLineAsync());
        await raw.SendAsync("alice {14}\r\n");
        Assert.Equal("+ Ready for literal data", await raw.ReadLineAsync());
        await raw.SendAsync("Test-Passw0rd!\r\n");
        Assert.Equal($"a1 OK [CAPABILITY {Capabilities}] Logged in", await raw.ReadLineAsync());

        await raw.SendAsync("a2 STATUS {5+}\r\nINBOX (MESSAGES)\r\n");
        Assert.Equal(new[] { "* STATUS \"INBOX\" (MESSAGES 0)", "a2 OK STATUS completed" }, await raw.ReadUntilTaggedAsync("a2"));

        // A mailbox name with an umlaut as a literal of raw UTF-8 is understood as well as the modified UTF-7 form.
        await raw.SendAsync("a3 CREATE {7+}\r\n");
        await raw.SendAsync(Encoding.UTF8.GetBytes("Gr\u00fc\u00dfe\r\n"));
        Assert.Equal(new[] { "a3 OK CREATE completed" }, await raw.ReadUntilTaggedAsync("a3"));
        Assert.Equal(new[] { "* STATUS \"Gr&APwA3w-e\" (MESSAGES 0)", "a4 OK STATUS completed" }, await raw.CommandAsync("a4", "STATUS Gr&APwA3w-e (MESSAGES)"));
    }

    [DbFact]
    public async Task Pipelined_commands_are_answered_in_order()
    {
        await AddToInboxAsync(ImapTestData.Simple("Pipelined"));
        await using RawImapClient raw = await ConnectAsync();
        await raw.SendAsync(
            "p1 NOOP\r\np2 CAPABILITY\r\np3 LOGIN alice Test-Passw0rd!\r\np4 SELECT INBOX\r\n" +
            "p5 FETCH 1 (UID FLAGS)\r\np6 STORE 1 +FLAGS (\\Seen)\r\np7 SEARCH SEEN\r\np8 LOGOUT\r\n");

        List<string> lines = await raw.ReadUntilTaggedAsync("p8");
        List<string> tagged = lines.Where(l => l.StartsWith('p')).Select(l => l[..2]).ToList();
        Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8" }, tagged);
        Assert.True(lines.IndexOf("* 1 FETCH (UID 1 FLAGS ())") < lines.FindIndex(l => l.StartsWith("p5 OK", StringComparison.Ordinal)));
        Assert.Contains("* 1 FETCH (UID 1 FLAGS (\\Seen))", lines);
        Assert.Contains("* SEARCH 1", lines);
    }

    [DbFact]
    public async Task Literals_over_the_limit_are_refused_and_the_session_goes_on()
    {
        await using RawImapClient raw = await ConnectAsync(login: true);
        await raw.SendAsync("a1 APPEND INBOX {200000000}\r\n");
        Assert.Equal("a1 NO [TOOBIG] The message is larger than the allowed 100 MB.", await raw.ReadLineAsync());
        Assert.Equal(new[] { "a2 OK NOOP completed" }, await raw.CommandAsync("a2", "NOOP"));

        await raw.SendAsync("a3 SELECT {2000000+}\r\n");
        await raw.SendAsync(new byte[2_000_000]);
        await raw.SendAsync("\r\n");
        Assert.Equal("a3 BAD The literal is too large.", await raw.ReadLineAsync());
        Assert.Equal(new[] { "a4 OK NOOP completed" }, await raw.CommandAsync("a4", "NOOP"));
    }

    [DbFact]
    public async Task Fetch_and_status_responses_use_the_documented_syntax()
    {
        MailMessage stored = await AddToInboxAsync(
            RawMail.Build("max@sender.test", "alice@example.test", "Hello", "Body", "<id@sender.test>"),
            m => m with { IsRead = true, IsStarred = true, Keywords = new[] { "$Label1" }, ReceivedDate = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc) });
        MailFolder inbox = await ImapTestData.FolderAsync(Host, Seed.AliceMailbox.Id, FolderKind.Inbox);
        await using RawImapClient raw = await ConnectAsync(login: true);
        await raw.CommandAsync("s", "SELECT INBOX");

        long size = stored.SizeBytes;
        Assert.Equal($"* 1 FETCH (FLAGS (\\Flagged \\Seen $Label1) UID 1 INTERNALDATE \"07-Oct-2026 08:00:00 +0000\" RFC822.SIZE {size})", (await raw.CommandAsync("f1", "FETCH 1 (FLAGS UID INTERNALDATE RFC822.SIZE)"))[0]);
        Assert.Equal($"* 1 FETCH (FLAGS (\\Flagged \\Seen $Label1) INTERNALDATE \"07-Oct-2026 08:00:00 +0000\" RFC822.SIZE {size})", (await raw.CommandAsync("f2", "FETCH 1 FAST"))[0]);
        string envelope = "(\"Tue, 07 Oct 2026 10:00:00 +0200\" \"Hello\" ((NIL NIL \"max\" \"sender.test\")) ((NIL NIL \"max\" \"sender.test\")) ((NIL NIL \"max\" \"sender.test\")) ((NIL NIL \"alice\" \"example.test\")) NIL NIL NIL \"<id@sender.test>\")";
        Assert.Equal($"* 1 FETCH (ENVELOPE {envelope})", (await raw.CommandAsync("f3", "FETCH 1 ENVELOPE"))[0]);
        Assert.Equal("* 1 FETCH (BODY (\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7BIT\" 6 1))", (await raw.CommandAsync("f4", "FETCH 1 (BODY)"))[0]);
        Assert.Equal("* 1 FETCH (BODYSTRUCTURE (\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7BIT\" 6 1 NIL NIL NIL NIL))", (await raw.CommandAsync("f5", "FETCH 1 BODYSTRUCTURE"))[0]);
        Assert.Equal($"* 1 FETCH (UID 1 FLAGS (\\Flagged \\Seen $Label1) INTERNALDATE \"07-Oct-2026 08:00:00 +0000\" RFC822.SIZE {size} ENVELOPE {envelope})", (await raw.CommandAsync("f6", "UID FETCH 1 ALL"))[0]);
        Assert.Contains(" BODY (\"text\" \"plain\"", (await raw.CommandAsync("f7", "FETCH 1 FULL"))[0]);
        Assert.Equal("* 1 FETCH (RFC822.TEXT {6}\r\nBody\r\n)", (await raw.CommandAsync("f8", "FETCH 1 RFC822.TEXT"))[0]);
        Assert.StartsWith("* 1 FETCH (RFC822.HEADER {", (await raw.CommandAsync("f9", "FETCH 1 RFC822.HEADER"))[0]);
        Assert.Equal("f10 BAD Unknown or unsupported fetch item BINARY.", (await raw.CommandAsync("f10", "FETCH 1 (BINARY[1])"))[^1]);
        Assert.Equal("f11 BAD Invalid body section.", (await raw.CommandAsync("f11", "FETCH 1 (BODY[MIME])"))[^1]);
        Assert.Equal("f12 BAD Unexpected text at the end of the command.", (await raw.CommandAsync("f12", "FETCH 1 (FLAGS) (CHANGEDSINCE 5)"))[^1]);
        Assert.Equal(
            new[] { $"* STATUS \"INBOX\" (MESSAGES 1 RECENT 0 UIDNEXT 2 UIDVALIDITY {inbox.UidValidity} UNSEEN 0)", "f13 OK STATUS completed" },
            await raw.CommandAsync("f13", "STATUS INBOX (MESSAGES RECENT UIDNEXT UIDVALIDITY UNSEEN)"));
    }

    [DbFact]
    public async Task Namespace_id_enable_unselect_and_check()
    {
        await AddToInboxAsync(ImapTestData.Simple("One"));
        await using RawImapClient raw = await ConnectAsync(login: true);
        Assert.Equal(new[] { "* NAMESPACE ((\"\" \"/\")) NIL ((\"Shared/\" \"/\"))", "n1 OK NAMESPACE completed" }, await raw.CommandAsync("n1", "NAMESPACE"));
        List<string> id = await raw.CommandAsync("n2", "ID NIL");
        Assert.Matches("^\\* ID \\(\"name\" \"MatMail\" \"version\" \".+\" \"vendor\" \"MatMail\"\\)$", id[0]);
        Assert.Equal(new[] { "* ENABLED", "n3 OK ENABLE completed" }, await raw.CommandAsync("n3", "ENABLE CONDSTORE QRESYNC"));
        await raw.CommandAsync("n4", "SELECT INBOX");
        Assert.Equal(new[] { "n5 OK CHECK completed" }, await raw.CommandAsync("n5", "CHECK"));
        Assert.Equal(new[] { "n6 BAD Not allowed while a mailbox is selected" }, await raw.CommandAsync("n6", "ENABLE CONDSTORE"));
        Assert.Equal(new[] { "n7 OK UNSELECT completed" }, await raw.CommandAsync("n7", "UNSELECT"));
        Assert.Equal(new[] { "n8 BAD No mailbox selected" }, await raw.CommandAsync("n8", "FETCH 1 FLAGS"));
    }

    [DbFact]
    public async Task Idle_ends_with_done_and_anything_else_is_bad()
    {
        await using RawImapClient raw = await ConnectAsync(login: true);
        await raw.SendAsync("i1 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());
        await raw.SendAsync("NOPE\r\n");
        Assert.Equal("i1 BAD Expected DONE", await raw.ReadLineAsync());

        await raw.SendAsync("i2 IDLE\r\n");
        Assert.Equal("+ idling", await raw.ReadLineAsync());
        await raw.SendAsync("done\r\n");
        Assert.Equal("i2 OK IDLE terminated", await raw.ReadLineAsync());
        Assert.Equal(new[] { "i3 OK NOOP completed" }, await raw.CommandAsync("i3", "NOOP"));
    }

    [DbFact]
    public async Task Close_expunges_silently_but_not_after_examine()
    {
        await AddToInboxAsync(ImapTestData.Simple("Doomed 1"), m => m with { IsDeleted = true });
        await AddToInboxAsync(ImapTestData.Simple("Doomed 2"), m => m with { IsDeleted = true });
        await AddToInboxAsync(ImapTestData.Simple("Stays"));
        MailFolder inbox = await ImapTestData.FolderAsync(Host, Seed.AliceMailbox.Id, FolderKind.Inbox);
        await using RawImapClient raw = await ConnectAsync(login: true);

        await raw.CommandAsync("c1", "EXAMINE INBOX");
        Assert.Equal(new[] { "c2 OK CLOSE completed" }, await raw.CommandAsync("c2", "CLOSE"));
        Assert.Equal(3, (await ImapTestData.MessagesAsync(Host, inbox.Id)).Count);

        await raw.CommandAsync("c3", "SELECT INBOX");
        Assert.Equal(new[] { "c4 OK CLOSE completed" }, await raw.CommandAsync("c4", "CLOSE"));
        Assert.Equal(new[] { "Stays" }, (await ImapTestData.MessagesAsync(Host, inbox.Id)).Select(m => m.Subject));
        Assert.Equal(new[] { "c5 BAD No mailbox selected" }, await raw.CommandAsync("c5", "CLOSE"));
    }

    [DbFact]
    public async Task Selecting_another_mailbox_leaves_the_first_one_and_a_failed_select_leaves_none()
    {
        await AddToInboxAsync(ImapTestData.Simple("One"));
        await using RawImapClient raw = await ConnectAsync(login: true);
        await raw.CommandAsync("s1", "SELECT INBOX");
        Assert.Contains("* 0 EXISTS", await raw.CommandAsync("s2", "SELECT Drafts"));

        // In an empty mailbox "*" has no value; any sequence set is simply empty there.
        Assert.Equal(new[] { "s3 OK FETCH completed" }, await raw.CommandAsync("s3", "FETCH 1:* FLAGS"));
        Assert.Equal(new[] { "s4 NO [NONEXISTENT] No such mailbox" }, await raw.CommandAsync("s4", "SELECT Nowhere"));
        Assert.Equal(new[] { "s5 BAD No mailbox selected" }, await raw.CommandAsync("s5", "FETCH 1 FLAGS"));
    }
}

/// <summary>The building blocks of the IMAP server that need no database.</summary>
public class ImapFormatTests
{
    private const string NestedMessage =
        "From: a@x.test\r\n" +
        "Subject: test\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"b1\"\r\n" +
        "\r\n" +
        "preamble\r\n" +
        "--b1\r\n" +
        "Content-Type: text/plain\r\n" +
        "\r\n" +
        "Hello\r\n" +
        "--b1\r\n" +
        "Content-Type: message/rfc822\r\n" +
        "\r\n" +
        "Subject: inner\r\n" +
        "Content-Type: multipart/alternative; boundary=\"b2\"\r\n" +
        "\r\n" +
        "--b2\r\n" +
        "Content-Type: text/plain\r\n" +
        "\r\n" +
        "inner text\r\n" +
        "two\r\n" +
        "--b2\r\n" +
        "Content-Type: text/html\r\n" +
        "\r\n" +
        "<p>x</p>\r\n" +
        "--b2--\r\n" +
        "\r\n" +
        "--b1\r\n" +
        "Content-Type: application/octet-stream; name=\"a.bin\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n" +
        "\r\n" +
        "AAEC\r\n" +
        "--b1--\r\n" +
        "epilogue\r\n";

    [Theory]
    [InlineData("INBOX", "INBOX")]
    [InlineData("Entwürfe", "Entw&APw-rfe")]
    [InlineData("Ärger & Co", "&AMQ-rger &- Co")]
    [InlineData("日本語", "&ZeVnLIqe-")]
    [InlineData("Grüße/Österreich", "Gr&APwA3w-e/&ANY-sterreich")]
    public void Mailbox_names_round_trip_through_modified_utf7(string name, string encoded)
    {
        Assert.Equal(encoded, ModifiedUtf7.Encode(name));
        Assert.Equal(name, ModifiedUtf7.Decode(encoded));
    }

    [Theory]
    [InlineData("a&b", "a&b")]
    [InlineData("&-&-", "&&")]
    [InlineData("x&!!-y", "x&!!-y")]
    public void Invalid_modified_utf7_is_kept_as_it_is(string encoded, string decoded)
        => Assert.Equal(decoded, ModifiedUtf7.Decode(encoded));

    [Fact]
    public void Sequence_sets_resolve_ranges_in_both_directions_and_the_star()
    {
        Assert.True(SequenceSet.TryParse("1:3,7,9:*", out SequenceSet? set));
        Assert.Equal(new (long, long)[] { (1, 3), (7, 7), (9, 12) }, set.Resolve(12));
        Assert.True(set.Contains(11, 12));
        Assert.False(set.Contains(8, 12));
        Assert.True(SequenceSet.TryParse("5:2", out SequenceSet? reversed));
        Assert.Equal(new (long, long)[] { (2, 5) }, reversed.Resolve(10));
        Assert.True(SequenceSet.TryParse("*:4", out SequenceSet? starFirst));
        Assert.Equal(new (long, long)[] { (3, 4) }, starFirst.Resolve(3));
        Assert.False(SequenceSet.TryParse("1:", out _));
        Assert.False(SequenceSet.TryParse("a", out _));
        Assert.False(SequenceSet.TryParse("1,,2", out _));
        Assert.Equal("1:3,7,9:10", SequenceSet.Format(new long[] { 1, 2, 3, 7, 9, 10 }));
    }

    [Fact]
    public void Dates_strings_and_flags_are_written_as_rfc_3501_wants()
    {
        Assert.Equal("\"07-Oct-2026 08:05:09 +0000\"", ImapFormat.InternalDate(new DateTime(2026, 10, 7, 8, 5, 9, DateTimeKind.Utc)));
        Assert.True(ImapFormat.TryParseDateTime(" 7-Oct-2026 23:15:00 -0700", out DateTime utc));
        Assert.Equal(new DateTime(2026, 10, 8, 6, 15, 0, DateTimeKind.Utc), utc);
        Assert.True(ImapFormat.TryParseDateTime("17-Jan-2025 01:02:03 +0130", out DateTime other));
        Assert.Equal(new DateTime(2025, 1, 16, 23, 32, 3, DateTimeKind.Utc), other);
        Assert.False(ImapFormat.TryParseDateTime("17-Foo-2025 01:02:03 +0000", out _));
        Assert.True(ImapFormat.TryParseDate("1-Feb-1994", out DateTime date));
        Assert.Equal(new DateTime(1994, 2, 1), date);

        Assert.Equal("\"say \\\"hi\\\" \\\\o/\"", ImapFormat.String("say \"hi\" \\o/"));
        Assert.Equal("{6}\r\nGrüß", ImapFormat.String("Grüß"));
        Assert.Equal("{3}\r\na\r\n", ImapFormat.String("a\r\n"));
        Assert.Equal("NIL", ImapFormat.NString(null));
        Assert.Equal("\"Entw&APw-rfe\"", ImapFormat.Mailbox("Entwürfe"));
        Assert.Equal("(\\Answered \\Seen $Forwarded Work)", ImapFlagNames.Format(ImapFlags.Seen | ImapFlags.Answered | ImapFlags.Forwarded, new[] { "Work" }));
    }

    [Fact]
    public void The_parser_reads_atoms_quoted_strings_literals_and_lists()
    {
        var request = new ImapRequest(new[] { "a1 LOGIN {5}", " \"pa\\\"ss\\\\\" (\\Seen $Label1) 1:3,5" }, new[] { Encoding.UTF8.GetBytes("alice") });
        var parser = new ImapParser(request);
        Assert.Equal("a1", parser.ReadTag());
        parser.ExpectSpace();
        Assert.Equal("LOGIN", parser.ReadAtom());
        parser.ExpectSpace();
        Assert.Equal("alice", parser.ReadAString());
        parser.ExpectSpace();
        Assert.Equal("pa\"ss\\", parser.ReadAString());
        parser.ExpectSpace();
        Assert.Equal(new[] { "\\Seen", "$Label1" }, parser.ReadFlagList());
        parser.ExpectSpace();
        Assert.Equal("1:3,5", parser.ReadSequenceSet().ToString());
        parser.ExpectEnd();

        Assert.True(ImapRequestReader.TryGetLiteralMarker("a1 APPEND INBOX {310+}", out long size, out bool synchronizing));
        Assert.Equal(310, size);
        Assert.False(synchronizing);
        Assert.False(ImapRequestReader.TryGetLiteralMarker("a1 LOGIN \"{5}\"", out _, out _));
    }

    [Fact]
    public void Body_sections_are_cut_from_the_original_bytes()
    {
        ImapMessageStructure structure = ImapMessageStructure.Parse(Encoding.ASCII.GetBytes(NestedMessage));
        string Section(string spec) => Encoding.ASCII.GetString(structure.GetSection(Parse(spec))!.Value.Span);

        Assert.Equal("Hello", Section("1"));
        Assert.Equal("Content-Type: text/plain\r\n\r\n", Section("1.MIME"));
        Assert.Equal("Subject: inner\r\nContent-Type: multipart/alternative; boundary=\"b2\"\r\n\r\n", Section("2.HEADER"));
        Assert.Equal("Subject: inner\r\n\r\n", Section("2.HEADER.FIELDS (subject)"));
        Assert.Equal("inner text\r\ntwo", Section("2.1"));
        Assert.Equal("<p>x</p>", Section("2.2"));
        Assert.Equal("Content-Type: text/html\r\n\r\n", Section("2.2.MIME"));
        Assert.StartsWith("--b2\r\nContent-Type: text/plain", Section("2.TEXT"));
        Assert.StartsWith("Subject: inner\r\n", Section("2"));
        Assert.EndsWith("--b2--\r\n", Section("2"));
        Assert.Equal("AAEC", Section("3"));
        Assert.StartsWith("preamble\r\n--b1", Section("TEXT"));
        Assert.EndsWith("epilogue\r\n", Section("TEXT"));
        Assert.Equal("From: a@x.test\r\nSubject: test\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n", Section("HEADER"));
        Assert.Null(structure.GetSection(Parse("4")));
        Assert.Null(structure.GetSection(Parse("1.1")));
        Assert.Null(structure.GetSection(Parse("1.HEADER")));
    }

    [Fact]
    public void A_single_part_message_has_exactly_part_one()
    {
        ImapMessageStructure structure = ImapMessageStructure.Parse(RawMail.Build("a@x.test", "b@y.test", "Single", "Only text"));
        Assert.Equal("Only text\r\n", Encoding.ASCII.GetString(structure.GetSection(Parse("1"))!.Value.Span));
        Assert.Null(structure.GetSection(Parse("2")));
        Assert.Equal("(\"text\" \"plain\" (\"charset\" \"utf-8\") NIL NIL \"7BIT\" 11 1 NIL NIL NIL NIL)", ImapBodyStructure.Build(structure, extensible: true));
    }

    [Theory]
    [InlineData("this is not a header\r\nneither is this\r\n", 0, 2)]
    [InlineData("", 0, 0)]
    [InlineData("\u0001\u0002 binary\r\n\r\nrest", 13, 1)]
    public void Messages_mime_cannot_read_become_one_text_body(string text, int headerLength, int lines)
    {
        byte[] raw = Encoding.ASCII.GetBytes(text);
        ImapMessageStructure structure = ImapMessageStructure.Parse(raw);
        Assert.Equal($"(\"text\" \"plain\" NIL NIL NIL \"7BIT\" {raw.Length - headerLength} {lines} NIL NIL NIL NIL)", ImapBodyStructure.Build(structure, extensible: true));
        Assert.Equal(text[headerLength..], Encoding.ASCII.GetString(structure.GetSection(Parse("TEXT"))!.Value.Span));
        Assert.Equal(text, Encoding.ASCII.GetString(structure.GetSection(Parse(""))!.Value.Span));
    }

    [Fact]
    public void Bodystructure_describes_nested_messages_with_exact_sizes()
    {
        ImapMessageStructure structure = ImapMessageStructure.Parse(Encoding.ASCII.GetBytes(NestedMessage));
        Assert.Equal(
            "((\"text\" \"plain\" NIL NIL NIL \"7BIT\" 5 1 NIL NIL NIL NIL)" +
            "(\"message\" \"rfc822\" NIL NIL NIL \"7BIT\" 172 (NIL \"inner\" NIL NIL NIL NIL NIL NIL NIL NIL) " +
            "((\"text\" \"plain\" NIL NIL NIL \"7BIT\" 15 2 NIL NIL NIL NIL)(\"text\" \"html\" NIL NIL NIL \"7BIT\" 8 1 NIL NIL NIL NIL) \"alternative\" (\"boundary\" \"b2\") NIL NIL NIL) 13 NIL NIL NIL NIL)" +
            "(\"application\" \"octet-stream\" (\"name\" \"a.bin\") NIL NIL \"BASE64\" 4 NIL NIL NIL NIL) \"mixed\" (\"boundary\" \"b1\") NIL NIL NIL)",
            ImapBodyStructure.Build(structure, extensible: true));
        Assert.Equal(
            "((\"text\" \"plain\" NIL NIL NIL \"7BIT\" 5 1)" +
            "(\"message\" \"rfc822\" NIL NIL NIL \"7BIT\" 172 (NIL \"inner\" NIL NIL NIL NIL NIL NIL NIL NIL) " +
            "((\"text\" \"plain\" NIL NIL NIL \"7BIT\" 15 2)(\"text\" \"html\" NIL NIL NIL \"7BIT\" 8 1) \"alternative\") 13)" +
            "(\"application\" \"octet-stream\" (\"name\" \"a.bin\") NIL NIL \"BASE64\" 4) \"mixed\")",
            ImapBodyStructure.Build(structure, extensible: false));
    }

    [Fact]
    public void Non_ascii_parameters_are_written_in_rfc_2231_form()
    {
        byte[] raw = Encoding.UTF8.GetBytes(
            "Content-Type: application/pdf; name*=utf-8''%C3%84rger.pdf\r\nContent-Disposition: attachment; filename=\"=?utf-8?q?=C3=84rger.pdf?=\"\r\n" +
            "Content-Language: de, en\r\nContent-ID: <part1@x.test>\r\nContent-Description: Gr\u00fc\u00dfe\r\n\r\nAAAA");
        string structure = ImapBodyStructure.Build(ImapMessageStructure.Parse(raw), extensible: true);
        Assert.StartsWith("(\"application\" \"pdf\" (\"name*\" \"utf-8''%C3%84rger.pdf\") \"<part1@x.test>\" \"=?utf-8?", structure);
        Assert.EndsWith("?=\" \"7BIT\" 4 NIL (\"attachment\" (\"filename*\" \"utf-8''%C3%84rger.pdf\")) (\"de\" \"en\") NIL)", structure);

        // The description is 7-bit (RFC 2047) on the wire and decodes back to the original text.
        int start = structure.IndexOf("\"=?utf-8?", StringComparison.Ordinal) + 1;
        string description = structure[start..structure.IndexOf("?=\"", start, StringComparison.Ordinal)] + "?=";
        Assert.Equal("Grüße", MimeKit.Utils.Rfc2047.DecodeText(Encoding.ASCII.GetBytes(description)));
    }

    [Fact]
    public void The_envelope_keeps_groups_and_encodes_names()
    {
        byte[] headers = Encoding.UTF8.GetBytes(
            "Date: Tue, 07 Oct 2026 10:00:00 +0200\r\n" +
            "Subject: =?utf-8?q?Gr=C3=BC=C3=9Fe?=\r\n" +
            " aus Köln\r\n" +
            "From: J\u00fcrgen <j@x.test>\r\n" +
            "Sender: secretary@x.test\r\n" +
            "To: Team: a@x.test, b@x.test;, plain@z.test\r\n" +
            "Cc: undisclosed-recipients:;\r\n" +
            "In-Reply-To: <parent@x.test>\r\n" +
            "Message-ID: <id@x.test>\r\n\r\n");
        string envelope = ImapEnvelope.Build(headers);

        Assert.StartsWith("(\"Tue, 07 Oct 2026 10:00:00 +0200\" \"=?utf-8?", envelope);
        Assert.Contains(" \"j\" \"x.test\")) ((NIL NIL \"secretary\" \"x.test\")) ((\"=?utf-8?", envelope);
        Assert.Contains(" ((NIL NIL \"Team\" NIL)(NIL NIL \"a\" \"x.test\")(NIL NIL \"b\" \"x.test\")(NIL NIL NIL NIL)(NIL NIL \"plain\" \"z.test\")) ", envelope);
        Assert.Contains(" ((NIL NIL \"undisclosed-recipients\" NIL)(NIL NIL NIL NIL)) NIL \"<parent@x.test>\" \"<id@x.test>\")", envelope);
        Assert.Equal("(NIL NIL NIL NIL NIL NIL NIL NIL NIL NIL)", ImapEnvelope.Build(Array.Empty<byte>()));
    }

    [Fact]
    public async Task AddImapServer_registers_one_hosted_server_that_honours_ports_set_to_off()
    {
        var config = new Configuration.AppConfig();
        config.Imap.Port = 0;
        config.Imap.ImplicitTlsPort = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        Services.ServiceRegistration.AddMatMailServices(services, config);
        services.AddImapServer();
        services.AddImapServer();

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        ImapServer server = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<ImapServer>());
        Assert.Same(server, provider.GetRequiredService<ImapServer>());

        await server.StartAsync(CancellationToken.None);
        await server.Started;
        Assert.Null(server.PlainEndpoint);
        Assert.Null(server.TlsEndpoint);
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Header_fields_keep_their_continuation_lines()
    {
        byte[] header = Encoding.ASCII.GetBytes("Subject: a long\r\n subject\r\nX-Other: 1\r\nReceived: from a\r\n\tby b\r\n\r\n");
        Assert.Equal("Subject: a long\r\n subject\r\nReceived: from a\r\n\tby b\r\n\r\n", Encoding.ASCII.GetString(ImapMessageStructure.FilterHeader(header, new[] { "SUBJECT", "received" }, include: true)));
        Assert.Equal("X-Other: 1\r\n\r\n", Encoding.ASCII.GetString(ImapMessageStructure.FilterHeader(header, new[] { "subject", "Received" }, include: false)));
    }

    private static ImapSection Parse(string spec)
    {
        var request = new ImapRequest(new[] { $"a FETCH 1 BODY[{spec}]" }, Array.Empty<byte[]>());
        var parser = new ImapParser(request);
        parser.ReadTag();
        parser.ExpectSpace();
        parser.ReadAtom();
        parser.ExpectSpace();
        parser.ReadSequenceSet();
        parser.ExpectSpace();
        return ImapFetchRequest.Parse(parser, isUid: false).Items.Single().Section!;
    }
}

/// <summary>Findings of the security review: what an administrator takes away must reach sessions that are already open.</summary>
public class ImapSecurityTests : ImapTestBase
{
    [DbFact]
    public async Task Withdrawing_a_delegation_ends_the_open_session_that_uses_it()
    {
        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Bob, MailboxAccess.Edit);
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;

        await using RawImapClient raw = await LoginRawAsync("bob");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT Shared/Info/INBOX"))[^1]);
        Assert.StartsWith("a2 OK", (await raw.CommandAsync("a2", "NOOP"))[^1]);

        using (IServiceScope scope = Host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailboxPermissions.Where(p => p.MailboxId == Seed.Info.Id && p.UserId == Seed.Bob.Id).ExecuteDeleteAsync();
        }

        await raw.SendAsync("a3 NOOP\r\n");
        Assert.StartsWith("* BYE", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task Reducing_a_delegation_to_read_makes_the_open_session_read_only()
    {
        await ImapTestData.GrantAsync(Host, Seed.Info, Seed.Bob, MailboxAccess.Edit);
        await ImapTestData.AddAsync(Host, Seed.Info.Id, FolderKind.Inbox, ImapTestData.Simple("For everybody"));
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;

        await using RawImapClient raw = await LoginRawAsync("bob");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT Shared/Info/INBOX"))[^1]);
        Assert.StartsWith("a2 OK", (await raw.CommandAsync("a2", "STORE 1 +FLAGS (\\Flagged)"))[^1]);

        using (IServiceScope scope = Host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailboxPermissions.Where(p => p.MailboxId == Seed.Info.Id && p.UserId == Seed.Bob.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Access, MailboxAccess.Read));
        }

        List<string> response = await raw.CommandAsync("a3", "STORE 1 -FLAGS (\\Flagged)");
        Assert.StartsWith("a3 NO", response[^1]);
        Assert.Contains("[NOPERM]", response[^1]);
    }

    [DbFact]
    public async Task Deactivating_the_user_ends_the_open_session()
    {
        Imap.Server.AccessRecheckInterval = TimeSpan.Zero;
        await using RawImapClient raw = await LoginRawAsync("alice");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT INBOX"))[^1]);

        using (IServiceScope scope = Host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == Seed.Alice.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        await raw.SendAsync("a2 NOOP\r\n");
        Assert.StartsWith("* BYE", await raw.ReadLineAsync());
        Assert.True(await raw.IsClosedAsync());
    }

    [DbFact]
    public async Task Absurdly_nested_search_criteria_are_refused_and_the_server_keeps_running()
    {
        await using RawImapClient raw = await LoginRawAsync("alice");
        Assert.StartsWith("a1 OK", (await raw.CommandAsync("a1", "SELECT INBOX"))[^1]);

        string groups = "a2 SEARCH " + new string('(', 20_000) + "ALL" + new string(')', 20_000);
        Assert.StartsWith("a2 BAD", (await raw.CommandAsync("a2", groups["a2 ".Length..]))[^1]);

        string nots = "SEARCH " + string.Concat(Enumerable.Repeat("NOT ", 20_000)) + "ALL";
        Assert.StartsWith("a3 BAD", (await raw.CommandAsync("a3", nots))[^1]);

        string ors = "SEARCH " + string.Concat(Enumerable.Repeat("OR ALL ", 20_000)) + "ALL";
        Assert.StartsWith("a4 BAD", (await raw.CommandAsync("a4", ors))[^1]);

        Assert.StartsWith("a5 OK", (await raw.CommandAsync("a5", "NOOP"))[^1]);
        Assert.StartsWith("a6 OK", (await raw.CommandAsync("a6", "SEARCH NOT (OR SEEN (FLAGGED UNSEEN))"))[^1]);
    }
}

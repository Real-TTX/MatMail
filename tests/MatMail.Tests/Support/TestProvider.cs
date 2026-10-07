using System.Net.Http.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MatMail.Tests.Support;

/// <summary>
/// Tests against a real mail provider (IMAP, POP3, SMTP) read <c>MATMAIL_TEST_IMAP</c>:
/// <c>host:imapPort;pop3=host:port;smtp=host:port;api=host:port</c>. "api" is the REST API of GreenMail, used to create a fresh
/// provider mailbox per test (default: the IMAP host, port 8080). Without the variable (or without <c>MATMAIL_TEST_DB</c>) the
/// tests are skipped.
/// <para>
/// GreenMail in Docker (default bridge network; authentication stays on, so wrong passwords are refused):
/// <code>
/// docker run -d --name matmail-test-greenmail -p 31025:3025 -p 31110:3110 -p 31143:3143 -p 31080:8080 \
///   -e GREENMAIL_OPTS="-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.api.hostname=0.0.0.0 -Dgreenmail.api.port=8080" \
///   greenmail/standalone:2.1.14
/// MATMAIL_TEST_IMAP=localhost:31143;pop3=localhost:31110;smtp=localhost:31025;api=localhost:31080
/// </code>
/// Remove it afterwards with <c>docker rm -f matmail-test-greenmail</c>.
/// </para>
/// </summary>
public static class TestProvider
{
    public static string? Setting => Environment.GetEnvironmentVariable("MATMAIL_TEST_IMAP");

    public static bool Available => !string.IsNullOrWhiteSpace(Setting) && TestDatabase.Available;

    public static (string Host, int Port) Imap => Endpoint(null, 3143);

    public static (string Host, int Port) Pop3 => Endpoint("pop3", 3110);

    public static (string Host, int Port) Smtp => Endpoint("smtp", 3025);

    public static (string Host, int Port) Api => Endpoint("api", 8080);

    private static (string Host, int Port) Endpoint(string? key, int defaultPort)
    {
        string[] parts = (Setting ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string imap = parts.FirstOrDefault(p => !p.Contains('=')) ?? "localhost:3143";
        string? value = key is null
            ? imap
            : parts.Where(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).Select(p => p[(key.Length + 1)..]).FirstOrDefault();
        return value is null ? (Parse(imap, defaultPort).Host, defaultPort) : Parse(value, defaultPort);
    }

    private static (string Host, int Port) Parse(string value, int defaultPort)
    {
        int colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value[(colon + 1)..], out int port) ? (value[..colon], port) : (value, defaultPort);
    }
}

public sealed class ProviderFactAttribute : FactAttribute
{
    public ProviderFactAttribute()
    {
        if (!TestProvider.Available)
        {
            Skip = "MATMAIL_TEST_IMAP / MATMAIL_TEST_DB are not set (needs a test mail server such as GreenMail and a PostgreSQL server).";
        }
    }
}

/// <summary>A mailbox at the test provider (a GreenMail user), with helpers to put mail in and to look at it.</summary>
public sealed record ProviderUser(string Login, string Password)
{
    public static async Task<ProviderUser> CreateAsync()
    {
        string login = "u" + Guid.NewGuid().ToString("N")[..12] + "@provider.test";
        string password = "Pw-" + Guid.NewGuid().ToString("N")[..12];
        (string host, int port) = TestProvider.Api;
        using var http = new HttpClient();
        using HttpResponseMessage response = await http.PostAsJsonAsync($"http://{host}:{port}/api/user", new { email = login, login, password });
        response.EnsureSuccessStatusCode();
        return new ProviderUser(login, password);
    }

    public async Task<ImapClient> ConnectAsync()
    {
        (string host, int port) = TestProvider.Imap;
        var client = new ImapClient { Timeout = 30_000 };
        await client.ConnectAsync(host, port, SecureSocketOptions.None);
        await client.AuthenticateAsync(Login, Password);
        return client;
    }

    /// <summary>Creates a folder; "/" separates levels ("Projects/2026"), whatever separator the server uses.</summary>
    public async Task CreateFolderAsync(string path)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder parent = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (string name in path.Split('/'))
        {
            IList<IMailFolder> children = await parent.GetSubfoldersAsync(false);
            parent = children.FirstOrDefault(f => f.Name == name)
                ?? await parent.CreateAsync(name, true)
                ?? throw new InvalidOperationException($"Folder {name} could not be created.");
        }

        await client.DisconnectAsync(true);
    }

    public async Task DeleteFolderAsync(string path)
    {
        using ImapClient client = await ConnectAsync();
        await (await FolderAsync(client, path)).DeleteAsync();
        await client.DisconnectAsync(true);
    }

    public Task<uint> AppendAsync(string folder, byte[] raw, MessageFlags flags = MessageFlags.None, DateTimeOffset? date = null)
        => AppendAsync(folder, MimeMessage.Load(new MemoryStream(raw)), flags, date);

    /// <summary>Puts a message into a folder (IMAP APPEND, so any header, flag and date is possible). Returns its UID.</summary>
    public async Task<uint> AppendAsync(string folder, MimeMessage message, MessageFlags flags = MessageFlags.None, DateTimeOffset? date = null)
    {
        using ImapClient client = await ConnectAsync();
        UniqueId? uid = await (await FolderAsync(client, folder)).AppendAsync(new AppendRequest(message, flags, date ?? DateTimeOffset.Now));
        await client.DisconnectAsync(true);
        return uid?.Id ?? 0;
    }

    /// <summary>Delivers a message to this mailbox through the provider's SMTP server (the way real mail arrives).</summary>
    public async Task SendAsync(byte[] raw)
    {
        (string host, int port) = TestProvider.Smtp;
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(host, port, SecureSocketOptions.None);
        MimeMessage message = MimeMessage.Load(new MemoryStream(raw));
        await client.SendAsync(message, MailboxAddress.Parse("sender@sender.test"), new[] { MailboxAddress.Parse(Login) });
        await client.DisconnectAsync(true);
    }

    public async Task<int> CountAsync(string folder)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder target = await FolderAsync(client, folder);
        await target.OpenAsync(FolderAccess.ReadOnly);
        int count = target.Count;
        await client.DisconnectAsync(true);
        return count;
    }

    public async Task<uint> UidValidityAsync(string folder)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder target = await FolderAsync(client, folder);
        await target.OpenAsync(FolderAccess.ReadOnly);
        uint validity = target.UidValidity;
        await client.DisconnectAsync(true);
        return validity;
    }

    public async Task<MessageFlags> FlagsAsync(string folder, uint uid)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder target = await FolderAsync(client, folder);
        await target.OpenAsync(FolderAccess.ReadOnly);
        IList<IMessageSummary> summaries = await target.FetchAsync(new[] { new UniqueId(target.UidValidity, uid) }, MessageSummaryItems.Flags);
        await client.DisconnectAsync(true);
        return summaries.Single().Flags ?? MessageFlags.None;
    }

    public async Task SetFlagsAsync(string folder, uint uid, MessageFlags flags, bool add = true)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder target = await FolderAsync(client, folder);
        await target.OpenAsync(FolderAccess.ReadWrite);
        var id = new UniqueId(target.UidValidity, uid);
        if (add)
        {
            await target.AddFlagsAsync(id, flags, true);
        }
        else
        {
            await target.RemoveFlagsAsync(id, flags, true);
        }

        await client.DisconnectAsync(true);
    }

    /// <summary>Removes a message for good (as another mail client would).</summary>
    public async Task ExpungeAsync(string folder, uint uid)
    {
        using ImapClient client = await ConnectAsync();
        IMailFolder target = await FolderAsync(client, folder);
        await target.OpenAsync(FolderAccess.ReadWrite);
        var id = new UniqueId(target.UidValidity, uid);
        await target.AddFlagsAsync(id, MessageFlags.Deleted, true);
        await target.ExpungeAsync(new[] { id });
        await client.DisconnectAsync(true);
    }

    private static async Task<IMailFolder> FolderAsync(ImapClient client, string path)
    {
        if (path.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return client.Inbox;
        }

        char separator = client.PersonalNamespaces[0].DirectorySeparator;
        return await client.GetFolderAsync(path.Replace('/', separator));
    }
}

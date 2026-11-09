using System.Net;
using MatMail.Configuration;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests;

/// <summary>
/// The public name of the server (<see cref="ServerName"/>): out of the box "localhost", which is wrong for a phone, for the certificate and for
/// other mail servers. It is learned from the address a platform administrator opens the web interface under, holds at once, and a name set on
/// purpose stays.
/// </summary>
public class ServerNameTests
{
    [Theory]
    [InlineData("mailbox.matthix.de", true)]
    [InlineData("MailBox.Matthix.DE.", true)]
    [InlineData("mail.example.com", true)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("[::1]", false)]
    [InlineData("matmail", false)]              // the name of the container in a Docker network (nginx passes it unless told otherwise)
    [InlineData("nas.local", false)]
    [InlineData("server.lan", false)]
    [InlineData("matmail.localhost", false)]
    public void Only_a_public_dns_name_can_be_the_name_of_the_server(string? name, bool expected)
        => Assert.Equal(expected, ServerName.IsPublicName(name));

    private static (AppConfig Config, string DataDir) NewInstallation(string hostname = "localhost")
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "matmail-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dataDir, "config"));
        var config = new AppConfig { DataDir = dataDir, Server = { Hostname = hostname } };
        AppConfigLoader.Save(dataDir, config);
        return (config, dataDir);
    }

    private static HttpRequest RequestFor(string host)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(host);
        return http.Request;
    }

    private static ActivityLogger Logger() => new ServiceCollection().AddLogging().BuildServiceProvider() is var provider
        ? new ActivityLogger(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActivityLogger>.Instance)
        : throw new InvalidOperationException();

    [Fact]
    public void Mail_programs_are_shown_the_name_the_page_was_opened_under_while_the_server_has_none()
    {
        (AppConfig unset, string a) = NewInstallation();
        (AppConfig named, string b) = NewInstallation("mail.firma.de");
        try
        {
            Assert.Equal("mailbox.matthix.de", new ServerName(unset, Logger()).ForRequest(RequestFor("mailbox.matthix.de:443")));
            Assert.Equal("localhost", new ServerName(unset, Logger()).ForRequest(RequestFor("localhost:9933")));      // nothing better known
            Assert.Equal("mail.firma.de", new ServerName(named, Logger()).ForRequest(RequestFor("mailbox.matthix.de")));   // a name set on purpose wins
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public async Task The_name_is_learned_once_saved_and_the_certificate_is_made_for_it()
    {
        (AppConfig config, string dataDir) = NewInstallation();
        try
        {
            using var certificates = new CertificateProvider(config, dataDir, NullLogger.Instance);
            Assert.Equal("CN=localhost", certificates.Describe()!.Subject);
            var serverName = new ServerName(config, Logger(), certificates);

            Assert.False(await serverName.LearnFromAsync(RequestFor("matmail:9933"), null));   // an internal name is no name
            Assert.True(await serverName.LearnFromAsync(RequestFor("mailbox.matthix.de"), null));

            Assert.Equal("mailbox.matthix.de", config.Server.Hostname);
            Assert.Equal("mailbox.matthix.de", AppConfigLoader.LoadFile(dataDir).Server.Hostname);     // it survives a restart
            Assert.Equal("CN=mailbox.matthix.de", certificates.Describe()!.Subject);              // the phone sees its name in the certificate

            Assert.False(await serverName.LearnFromAsync(RequestFor("other.example.org"), null));  // once: it is set now
            Assert.Equal("mailbox.matthix.de", config.Server.Hostname);

            serverName.Apply("mail.firma.de");                                                    // the server settings: at once
            Assert.Equal("CN=mail.firma.de", certificates.Describe()!.Subject);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [DbFact]
    public async Task The_mail_servers_greet_with_the_name_as_soon_as_it_is_known()
    {
        await using TestHost host = await TestHost.CreateAsync(c => c.Server.Hostname = "localhost");
        await host.SeedAsync();
        await using ImapTestServer imap = await ImapTestServer.StartAsync(host);

        new ServerName(host.Config, Logger()).Apply("mailbox.matthix.de");

        await using RawImapClient client = await RawImapClient.ConnectAsync(imap.PlainPort);
        string greeting = await client.ReadLineAsync();
        Assert.Contains("mailbox.matthix.de", greeting);
    }
}

/// <summary>The page for mail programs and the learning through the web, with the whole app in memory.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class ServerNameWebTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    [DbFact]
    public async Task The_page_for_mail_programs_names_the_public_name_and_a_platform_administrator_teaches_it_to_the_server()
    {
        HttpClient admin = site.Browser();
        admin.DefaultRequestHeaders.Host = "mailbox.matthix.de";
        await SignIn.LocalAsync(admin, "/Account/Login");

        HttpResponseMessage response = await admin.GetAsync("/Account/MailPrograms");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.Headers.Location}");
        // (IMAP and SMTP are off in this test host, so the page shows no server; what it would show is ServerName.ForRequest, tested above)

        AppConfig config = site.Services.GetRequiredService<AppConfig>();
        Assert.Equal("mailbox.matthix.de", config.Server.Hostname);   // learned from the request of the platform administrator
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Platform/Settings")).StatusCode);
    }
}

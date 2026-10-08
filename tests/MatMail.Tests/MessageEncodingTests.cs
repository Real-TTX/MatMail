using System.Text;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>Footers, signatures and templates are written in Unicode; the messages they go into are not always.</summary>
public class MessageEncodingTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.Signatures.Add(new Signature { TenantId = _seed.Tenant.Id, Name = "Legal", Kind = SignatureKind.Footer, Html = "<p>Geschäftsführer: Müller · Preis 5 € · 日本語</p>" });
        db.MailTemplates.Add(new MailTemplate { TenantId = _seed.Tenant.Id, Name = "Frame", Html = "<div><h1>Grüße von {{Tenant}} €</h1><div>{{Body}}</div></div>", ForMailPrograms = true });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>A plain-text message in a legacy character set, as an old mail program writes it.</summary>
    private static MimeMessage Latin1Message(string text)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("bob@example.test"));
        message.Subject = "Grüße";
        var part = new TextPart("plain");
        part.SetText(Encoding.GetEncoding("iso-8859-1"), text);
        message.Body = part;
        return message;
    }

    private async Task<MimeMessage> SubmitAndReadAsync(MimeMessage message)
    {
        using IServiceScope scope = _host.Scope();
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = MimeSerializer.ToBytes(message),
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test" },
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
        });
        Assert.True(result.Accepted, result.Error);

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailMessage delivered = db.MailMessages.Single(m => m.MailboxId == _seed.BobMailbox.Id);
        byte[] raw = (await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(delivered.Id))!;
        return await MimeMessage.LoadAsync(new MemoryStream(raw));
    }

    [DbFact]
    public async Task Characters_of_the_footer_survive_in_a_message_of_a_legacy_character_set()
    {
        MimeMessage mail = await SubmitAndReadAsync(Latin1Message("Viele Grüße aus Köln"));

        string text = mail.TextBody!;
        Assert.Contains("Viele Grüße aus Köln", text);
        Assert.Contains("Geschäftsführer: Müller · Preis 5 € · 日本語", text);

        // Numeric entities for the umlauts are fine in HTML; what matters is what a reader sees.
        string html = System.Net.WebUtility.HtmlDecode(mail.HtmlBody!);
        Assert.Contains("Grüße von Home €", html);
        Assert.Contains("Viele Grüße aus Köln", html);
        Assert.Contains("Geschäftsführer: Müller · Preis 5 € · 日本語", html);
    }

    [DbFact]
    public async Task A_message_in_a_character_set_without_a_name_the_server_knows_still_goes_out()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("bob@example.test"));
        message.Subject = "Odd";
        string raw = "From: alice@example.test\r\nTo: bob@example.test\r\nSubject: Odd\r\nMessage-ID: <odd@example.test>\r\nMIME-Version: 1.0\r\n" +
                     "Content-Type: text/plain; charset=x-no-such-charset\r\nContent-Transfer-Encoding: 8bit\r\n\r\nHello there\r\n";

        using IServiceScope scope = _host.Scope();
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = Encoding.ASCII.GetBytes(raw),
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test" },
            TenantId = _seed.Tenant.Id,
            MailboxId = _seed.AliceMailbox.Id,
            SenderUserId = _seed.Alice.Id,
        });

        Assert.True(result.Accepted, result.Error);
    }
}

public class TransferEncodingTests
{
    private static MimeMessage Parse(string transferEncoding)
    {
        string raw = "From: a@example.test\r\nTo: b@example.test\r\nSubject: x\r\nMIME-Version: 1.0\r\n" +
                     "Content-Type: text/plain; charset=us-ascii\r\nContent-Transfer-Encoding: " + transferEncoding + "\r\n\r\nPlain ascii text\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));
    }

    [Theory]
    [InlineData("7bit")]
    [InlineData("8bit")]
    [InlineData("quoted-printable")]
    [InlineData("base64")]
    public void A_text_with_umlauts_written_into_a_part_leaves_with_an_encoding_that_fits(string transferEncoding)
    {
        MimeMessage message = Parse(transferEncoding);
        var part = (TextPart)message.Body!;

        MessageContent.SetText(part, "Grüße aus Köln €");

        byte[] bytes = MimeSerializer.ToBytes(message);
        string wire = Encoding.ASCII.GetString(bytes);
        string header = wire[..wire.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

        // Either the text is encoded (quoted-printable, base64) or the message says it is 8-bit; never 8-bit bytes under a 7bit label.
        bool highBytes = bytes.Any(b => b > 127);
        bool labelledSevenBit = !header.Contains("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || header.Contains("Content-Transfer-Encoding: 7bit", StringComparison.OrdinalIgnoreCase);
        Assert.False(highBytes && labelledSevenBit, header);

        MimeMessage roundTrip = MimeMessage.Load(new MemoryStream(bytes));
        Assert.Equal("Grüße aus Köln €", roundTrip.TextBody!.TrimEnd());
    }
}

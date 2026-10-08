using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

public class SignatureTemplateTests
{
    [Fact]
    public void Placeholders_survive_the_cleaning_even_inside_attributes()
    {
        string clean = SignatureService.SanitizeTemplate("<p onclick=\"x()\"><a href=\"mailto:{{Email}}\">{{FullName}}</a> <a href=\"{{Website}}\">site</a><script>1</script></p>");

        Assert.Contains("href=\"mailto:{{Email}}\"", clean);
        Assert.Contains("href=\"{{Website}}\"", clean);
        Assert.Contains(">{{FullName}}<", clean);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("<script", clean);
    }

    [Fact]
    public void Only_pictures_may_be_embedded_as_data_addresses()
    {
        const string png = "data:image/png;base64,iVBORw0KGgo=";
        string clean = SignatureService.SanitizeTemplate($"<img src=\"{png}\"><a href=\"{png}\">x</a><img src=\"data:text/html;base64,PHNjcmlwdD4=\"><div style=\"background:url({png})\">y</div>");

        Assert.Contains($"src=\"{png}\"", clean);
        Assert.DoesNotContain("<a href=\"data:", clean);
        Assert.DoesNotContain("text/html", clean);
        Assert.DoesNotContain("url(", clean);
    }

    private static SignatureContext Sender(string? phone = null, string? mobile = null)
        => new("Alice Example", "alice@example.test", null, phone, "Home") { Mobile = mobile };

    [Fact]
    public void A_line_without_any_value_is_left_out_together_with_its_label()
    {
        const string template = "<p>{{FullName}}<br>Phone: {{Phone}}<br>{{Tenant}}</p><p>Mobile: {{Mobile}}</p><p><a href=\"mailto:{{Email}}\">{{Email}}</a></p>";

        string html = SignatureService.Render(template, Sender(), html: true);

        Assert.Equal("<p>Alice Example<br>Home</p><p><a href=\"mailto:alice@example.test\">alice@example.test</a></p>", html);
    }

    [Fact]
    public void A_line_stays_as_long_as_one_of_its_placeholders_has_a_value()
    {
        string html = SignatureService.Render("<p>Phone: {{Phone}} / Mobile: {{Mobile}}</p>", Sender(mobile: "+49 170 1"), html: true);

        Assert.Equal("<p>Phone:  / Mobile: +49 170 1</p>", html);
    }

    [Fact]
    public void Lines_in_a_separate_block_each_and_in_a_single_block_are_treated_alike()
    {
        string html = SignatureService.Render("<div>Hello</div><div><b>Phone:</b> {{Phone}}</div><div>Tel {{Phone}}<br>{{Email}}</div>", Sender(), html: true);

        Assert.Equal("<div>Hello</div><div>alice@example.test</div>", html);
    }

    [Fact]
    public void A_table_keeps_its_shape_when_a_value_is_missing()
    {
        string html = SignatureService.Render("<table><tbody><tr><td>Phone</td><td>{{Phone}}</td></tr><tr><td>Mail</td><td>{{Email}}</td></tr></tbody></table>", Sender(), html: true);

        Assert.Equal("<table><tbody><tr><td>Phone</td><td></td></tr><tr><td>Mail</td><td>alice@example.test</td></tr></tbody></table>", html);
    }

    [Fact]
    public void The_place_of_the_body_and_unknown_placeholders_are_never_taken_for_empty_lines()
    {
        string html = SignatureService.Render("<p>{{Body}}</p><p>{{Nonsense}}</p>", Sender(), html: true);

        Assert.Equal("<p>{{Body}}</p><p>{{Nonsense}}</p>", html);
    }

    [Fact]
    public void Plain_text_loses_the_same_lines_and_keeps_its_line_ends()
    {
        string text = SignatureService.Render("{{FullName}}\r\nPhone: {{Phone}}\r\nMobile: {{Mobile}}\r\n{{Tenant}}", Sender(mobile: "+49 170 1"), html: false);

        Assert.Equal("Alice Example\r\nMobile: +49 170 1\r\nHome", text);
    }

    [Fact]
    public void A_template_without_placeholders_is_not_touched()
    {
        const string template = "<p>Hello<br>World</p>";

        Assert.Equal(template, SignatureService.Render(template, Sender(), html: true));
    }
}

public class SignatureApplyTests : IAsyncLifetime
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

    private async Task AddAsync(string name, SignatureKind kind, AppliesTo scope, string html, bool addOnServer = false, bool isDefault = false, long? userId = null)
    {
        using IServiceScope scope0 = _host.Scope();
        var db = scope0.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.Signatures.Add(new Signature
        {
            TenantId = _seed.Tenant.Id, Name = name, Kind = kind, Scope = scope, Html = html, AddOnServer = addOnServer, IsDefault = isDefault, UserId = userId,
        });
        await db.SaveChangesAsync();
    }

    private static MimeMessage Message(string html, string text)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Subject = "Hello";
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();
        return message;
    }

    private static string HtmlOf(MimeMessage message) => message.HtmlBody ?? throw new InvalidOperationException("The message has no HTML part.");

    private static string TextOf(MimeMessage message) => message.TextBody ?? throw new InvalidOperationException("The message has no text part.");

    private async Task ApplyAsync(MimeMessage message, SubmissionSource source)
    {
        using IServiceScope scope = _host.Scope();
        var context = new SignatureContext("Alice Example", "alice@example.test", "CTO", "+49 1", "Home");
        await scope.ServiceProvider.GetRequiredService<SignatureService>()
            .ApplyAsync(message, source, _seed.Tenant.Id, _seed.AliceMailbox.Id, _seed.Alice.Id, context);
    }

    [DbFact]
    public async Task A_signature_set_to_be_added_reaches_messages_of_mail_programs_but_not_those_of_the_web_client()
    {
        await AddAsync("Company", SignatureKind.Signature, AppliesTo.Tenant, "<p>Regards, {{DisplayName}}</p>", addOnServer: true);

        MimeMessage fromOutlook = Message("<html><body><p>Hi</p></body></html>", "Hi");
        await ApplyAsync(fromOutlook, SubmissionSource.MailProgram);
        Assert.Contains("<div class=\"mm-signature\"><p>Regards, Alice Example</p></div></body>", HtmlOf(fromOutlook));
        Assert.Contains("-- \r\nRegards, Alice Example", TextOf(fromOutlook).ReplaceLineEndings("\r\n"));

        MimeMessage fromWeb = Message("<html><body><p>Hi</p></body></html>", "Hi");
        await ApplyAsync(fromWeb, SubmissionSource.Web);
        Assert.DoesNotContain("Regards", HtmlOf(fromWeb));
        Assert.DoesNotContain("Regards", TextOf(fromWeb));
    }

    [DbFact]
    public async Task A_message_that_carries_the_signature_does_not_get_it_twice()
    {
        await AddAsync("Company", SignatureKind.Signature, AppliesTo.Tenant, "<p>Regards, {{DisplayName}}</p>", addOnServer: true);

        MimeMessage message = Message(
            "<div>Hi</div><div class=\"mm-signature\">-- <br><p>Regards, Alice Example</p></div>",
            "Hi\r\n\r\n-- \r\nRegards, Alice Example");
        await ApplyAsync(message, SubmissionSource.MailProgram);

        Assert.Equal(1, HtmlOf(message).Split("Regards").Length - 1);
        Assert.Equal(1, TextOf(message).Split("Regards").Length - 1);
    }

    [DbFact]
    public async Task Signatures_that_are_only_offered_are_not_added_and_footers_always_are()
    {
        await AddAsync("Offered", SignatureKind.Signature, AppliesTo.Tenant, "<p>OFFERED</p>");
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, "<p>LEGAL</p>");

        foreach (SubmissionSource source in new[] { SubmissionSource.MailProgram, SubmissionSource.Web, SubmissionSource.SmartHost })
        {
            MimeMessage message = Message("<html><body><p>Hi</p></body></html>", "Hi");
            await ApplyAsync(message, source);
            Assert.DoesNotContain("OFFERED", HtmlOf(message));
            Assert.Contains("LEGAL", HtmlOf(message));
            Assert.Contains("LEGAL", TextOf(message));
        }
    }

    [DbFact]
    public async Task The_signature_of_the_user_wins_over_the_one_of_the_tenant_and_comes_before_the_footer()
    {
        await AddAsync("Tenant", SignatureKind.Signature, AppliesTo.Tenant, "<p>TENANT</p>", addOnServer: true, isDefault: true);
        await AddAsync("Alice", SignatureKind.Signature, AppliesTo.User, "<p>ALICE</p>", addOnServer: true, userId: _seed.Alice.Id);
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, "<p>LEGAL</p>");

        MimeMessage message = Message("<html><body><p>Hi</p></body></html>", "Hi");
        await ApplyAsync(message, SubmissionSource.MailProgram);

        string html = HtmlOf(message);
        Assert.Contains("ALICE", html);
        Assert.DoesNotContain("TENANT", html);
        Assert.True(html.IndexOf("ALICE", StringComparison.Ordinal) < html.IndexOf("LEGAL", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task Pictures_of_a_footer_travel_as_inline_parts_of_the_message()
    {
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, $"<p><img src=\"data:image/png;base64,{Png}\" width=\"40\"> LEGAL</p>");

        MimeMessage message = Message("<html><body><p>Hi</p></body></html>", "Hi");
        await ApplyAsync(message, SubmissionSource.MailProgram);

        Assert.DoesNotContain("data:image", HtmlOf(message));
        MimePart picture = Assert.Single(message.BodyParts.OfType<MimePart>(), p => p.ContentType.MimeType == "image/png");
        Assert.NotNull(picture.ContentId);
        Assert.Contains("src=\"cid:" + picture.ContentId + "\"", HtmlOf(message));

        // The HTML and its picture live together in one multipart/related, which in turn sits next to the plain-text version.
        TextPart html = message.BodyParts.OfType<TextPart>().Single(p => p.IsHtml);
        Assert.NotNull(message.Body);
        Multipart related = Assert.IsAssignableFrom<Multipart>(ParentOf(message.Body, picture));
        Assert.True(related.ContentType.IsMimeType("multipart", "related"));
        Assert.Contains(html, related);
        Assert.Equal("multipart/alternative", message.Body.ContentType.MimeType);
    }

    [DbFact]
    public async Task A_footer_picture_goes_into_the_existing_related_part_of_a_message_that_has_one()
    {
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, $"<p><img src=\"data:image/png;base64,{Png}\"></p>");

        var existing = new MimePart("image", "gif") { Content = new MimeContent(new MemoryStream(new byte[] { 71, 73, 70 })), ContentId = "own@picture", ContentDisposition = new ContentDisposition(ContentDisposition.Inline) };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Body = new Multipart("related") { new TextPart("html") { Text = "<html><body><img src=\"cid:own@picture\"></body></html>" }, existing };

        await ApplyAsync(message, SubmissionSource.MailProgram);

        var related = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.True(related.ContentType.IsMimeType("multipart", "related"));
        Assert.Equal(3, related.Count);
        Assert.Contains("src=\"cid:own@picture\"", HtmlOf(message));
        Assert.DoesNotContain("data:image", HtmlOf(message));
    }

    [DbFact]
    public async Task Signed_messages_are_left_exactly_as_they_were_written()
    {
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, "<p>LEGAL</p>");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Body = new Multipart("signed")
        {
            new TextPart("plain") { Text = "Signed text" },
            new MimePart("application", "pgp-signature") { Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })) },
        };

        await ApplyAsync(message, SubmissionSource.MailProgram);

        TextPart text = message.BodyParts.OfType<TextPart>().Single();
        Assert.Equal("Signed text", text.Text);
    }

    [DbFact]
    public async Task A_message_without_any_html_only_gets_the_text_of_the_footer()
    {
        await AddAsync("Legal", SignatureKind.Footer, AppliesTo.Tenant, $"<p><img src=\"data:image/png;base64,{Png}\"> LEGAL</p>");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Body = new TextPart("plain") { Text = "Hi" };

        await ApplyAsync(message, SubmissionSource.SmartHost);

        TextPart body = Assert.IsType<TextPart>(message.Body);
        Assert.Contains("Hi", body.Text);
        Assert.Contains("-- \r\nLEGAL", body.Text.ReplaceLineEndings("\r\n"));
        Assert.DoesNotContain("data:image", body.Text);
    }

    private static Multipart? ParentOf(MimeEntity root, MimeEntity target)
    {
        if (root is not Multipart multipart)
        {
            return null;
        }

        foreach (MimeEntity child in multipart)
        {
            if (ReferenceEquals(child, target))
            {
                return multipart;
            }

            Multipart? deeper = ParentOf(child, target);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }
}

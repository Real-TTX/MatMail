using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

public class TemplateTextTests
{
    [Fact]
    public void A_template_needs_a_place_for_the_message()
    {
        Assert.True(TemplateService.HasPlaceForBody("<div>{{Body}}</div>"));
        Assert.True(TemplateService.HasPlaceForBody("<p>{{ body }}</p>"));
        Assert.False(TemplateService.HasPlaceForBody("<p>{{Tenant}}</p>"));
        Assert.False(TemplateService.HasPlaceForBody(string.Empty));
    }
}

public class TemplateTests : IAsyncLifetime
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private const string Frame = "<div><h1>{{Tenant}}</h1><div>{{Body}}</div></div>";

    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<MailTemplate> AddAsync(string name, Action<MailTemplate>? configure = null)
    {
        var template = new MailTemplate { TenantId = _seed.Tenant.Id, Name = name, Html = Frame };
        configure?.Invoke(template);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private async Task<RelayRule> AddRuleAsync(string name)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var rule = new RelayRule { TenantId = _seed.Tenant.Id, Name = name, Network = "10.0.0.0/8" };
        db.RelayRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    private static MimeMessage PlainMessage(string text)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Subject = "Report";
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }

    private static MimeMessage HtmlMessage(string html)
    {
        MimeMessage message = PlainMessage("Hello");
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = "Hello" }.ToMessageBody();
        return message;
    }

    private async Task<MailTemplate?> ApplyAsync(MimeMessage message, SubmissionSource source, long? relayRuleId = null, long? userId = null, long? mailboxId = null)
    {
        using IServiceScope scope = _host.Scope();
        var context = new SignatureContext("Alice Example", "alice@example.test", null, null, "Home");
        return await scope.ServiceProvider.GetRequiredService<TemplateService>()
            .ApplyAsync(message, source, _seed.Tenant.Id, mailboxId ?? _seed.AliceMailbox.Id, userId ?? _seed.Alice.Id, relayRuleId, context);
    }

    private static string HtmlOf(MimeMessage message) => message.HtmlBody ?? throw new InvalidOperationException("The message has no HTML part.");

    [DbFact]
    public async Task A_plain_text_message_becomes_an_html_mail_with_the_text_kept_next_to_it()
    {
        await AddAsync("Frame");
        MimeMessage message = PlainMessage("Backup finished.\r\nSee https://status.example.com/run?id=1&x=2.\r\nCosts: <5 & \"free\"");

        MailTemplate? applied = await ApplyAsync(message, SubmissionSource.SmartHost);

        Assert.NotNull(applied);
        var alternative = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.True(alternative.ContentType.IsMimeType("multipart", "alternative"));
        Assert.Equal("Backup finished.\r\nSee https://status.example.com/run?id=1&x=2.\r\nCosts: <5 & \"free\"", message.TextBody?.ReplaceLineEndings("\r\n"));

        string html = HtmlOf(message);
        Assert.Contains("<h1>Home</h1>", html);
        Assert.Contains("Backup finished.<br>", html);
        Assert.Contains("<a href=\"https://status.example.com/run?id=1&amp;x=2\">https://status.example.com/run?id=1&amp;x=2</a>.<br>", html);
        Assert.Contains("Costs: &lt;5 &amp; &quot;free&quot;", html);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [DbFact]
    public async Task The_body_is_inserted_as_it_is_even_when_it_looks_like_a_replacement_pattern()
    {
        await AddAsync("Frame");
        MimeMessage message = PlainMessage("Price: $1 $& $` $' ${Body}");

        await ApplyAsync(message, SubmissionSource.SmartHost);

        Assert.Contains("Price: $1 $&amp; $` $&#39; ${Body}", HtmlOf(message));
    }

    [DbFact]
    public async Task Attachments_stay_where_they_were()
    {
        await AddAsync("Frame");
        var message = PlainMessage("see attachment");
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "see attachment" },
            new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })), FileName = "report.pdf", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) },
        };

        await ApplyAsync(message, SubmissionSource.MailProgram);

        var mixed = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.True(mixed.ContentType.IsMimeType("multipart", "mixed"));
        Assert.Equal(2, mixed.Count);
        var alternative = Assert.IsAssignableFrom<Multipart>(mixed[0]);
        Assert.True(alternative.ContentType.IsMimeType("multipart", "alternative"));
        Assert.Equal(new[] { "text/plain", "text/html" }, alternative.Select(part => part.ContentType.MimeType));
        Assert.Equal("report.pdf", ((MimePart)mixed[1]).FileName);
    }

    [DbFact]
    public async Task A_message_that_has_html_is_only_wrapped_when_the_template_is_for_every_message()
    {
        await AddAsync("Frame");
        const string original = "<html><head><style>p { color: red }</style></head><body class=\"x\"><p>Hello</p></body></html>";
        MimeMessage untouched = HtmlMessage(original);
        Assert.Null(await ApplyAsync(untouched, SubmissionSource.MailProgram));
        Assert.Equal(original, HtmlOf(untouched));

        await AddAsync("Everything", t => { t.Mode = TemplateMode.AllMessages; t.Priority = 1; });
        MimeMessage wrapped = HtmlMessage(original);
        Assert.NotNull(await ApplyAsync(wrapped, SubmissionSource.MailProgram));

        // The head and the attributes of the body stay; the content of the body went into the template.
        Assert.Equal("<html><head><style>p { color: red }</style></head><body class=\"x\"><div><h1>Home</h1><div><p>Hello</p></div></div></body></html>", HtmlOf(wrapped));
        Assert.Equal("Hello", wrapped.TextBody);
    }

    [DbFact]
    public async Task A_template_applies_only_to_the_sources_it_was_made_for()
    {
        await AddAsync("Devices", t => { t.ForWebClient = false; t.ForMailPrograms = false; t.ForSmartHost = true; });

        Assert.NotNull(await ApplyAsync(PlainMessage("x"), SubmissionSource.SmartHost));
        Assert.Null(await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram));
        Assert.Null(await ApplyAsync(PlainMessage("x"), SubmissionSource.Web));

        await AddAsync("Web", t => { t.ForWebClient = true; t.ForMailPrograms = false; t.ForSmartHost = false; t.Name = "Web"; });
        MailTemplate? forWeb = await ApplyAsync(PlainMessage("x"), SubmissionSource.Web);
        Assert.Equal("Web", forWeb?.Name);
    }

    [DbFact]
    public async Task A_template_for_one_smart_host_rule_applies_to_that_rule_and_wins_over_the_general_one()
    {
        RelayRule printers = await AddRuleAsync("Printers");
        RelayRule scripts = await AddRuleAsync("Scripts");
        await AddAsync("General");
        await AddAsync("Printers only", t => { t.RelayRuleId = printers.Id; t.Priority = 500; });

        Assert.Equal("Printers only", (await ApplyAsync(PlainMessage("x"), SubmissionSource.SmartHost, printers.Id))?.Name);
        Assert.Equal("General", (await ApplyAsync(PlainMessage("x"), SubmissionSource.SmartHost, scripts.Id))?.Name);
        Assert.Equal("General", (await ApplyAsync(PlainMessage("x"), SubmissionSource.SmartHost))?.Name);

        // Mail programs never come through a rule: the template that is bound to one does not apply to them.
        Assert.Equal("General", (await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram, printers.Id))?.Name);
    }

    [DbFact]
    public async Task The_most_specific_template_wins_and_then_the_lower_number()
    {
        await AddAsync("Tenant, late", t => t.Priority = 50);
        await AddAsync("Tenant, early", t => t.Priority = 10);
        Assert.Equal("Tenant, early", (await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram))?.Name);

        await AddAsync("Mailbox of Alice", t => { t.Scope = AppliesTo.Mailbox; t.MailboxId = _seed.AliceMailbox.Id; t.Priority = 900; });
        Assert.Equal("Mailbox of Alice", (await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram))?.Name);
        Assert.Equal("Tenant, early", (await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram, mailboxId: _seed.BobMailbox.Id, userId: _seed.Bob.Id))?.Name);

        await AddAsync("Alice herself", t => { t.Scope = AppliesTo.User; t.UserId = _seed.Alice.Id; t.Priority = 9000; });
        Assert.Equal("Alice herself", (await ApplyAsync(PlainMessage("x"), SubmissionSource.MailProgram))?.Name);
    }

    [DbFact]
    public async Task Inactive_templates_and_templates_without_a_place_for_the_body_are_skipped()
    {
        await AddAsync("Inactive", t => { t.IsActive = false; t.Priority = 1; });
        await AddAsync("Swallows the message", t => { t.Html = "<p>Only a header</p>"; t.Priority = 2; });
        await AddAsync("Good one", t => t.Priority = 3);

        MimeMessage message = PlainMessage("Keep me");
        MailTemplate? applied = await ApplyAsync(message, SubmissionSource.MailProgram);

        Assert.Equal("Good one", applied?.Name);
        Assert.Contains("Keep me", HtmlOf(message));
    }

    [DbFact]
    public async Task A_template_that_is_a_paragraph_around_the_body_does_not_nest_the_paragraphs_of_the_message()
    {
        await AddAsync("Paragraph", t => { t.Html = "<p>{{Body}}</p>"; t.Mode = TemplateMode.AllMessages; });
        MimeMessage message = HtmlMessage("<html><body><p>One</p><p>Two</p></body></html>");

        await ApplyAsync(message, SubmissionSource.MailProgram);

        Assert.Contains("<body><div><p>One</p><p>Two</p></div></body>", HtmlOf(message));
    }

    [DbFact]
    public async Task Signed_messages_and_reports_are_never_put_into_a_template()
    {
        await AddAsync("Frame", t => t.Mode = TemplateMode.AllMessages);

        MimeMessage signed = PlainMessage("x");
        signed.Body = new Multipart("signed")
        {
            new TextPart("plain") { Text = "Signed text" },
            new MimePart("application", "pgp-signature") { Content = new MimeContent(new MemoryStream(new byte[] { 1 })) },
        };
        Assert.Null(await ApplyAsync(signed, SubmissionSource.MailProgram));
        Assert.Equal("Signed text", signed.BodyParts.OfType<TextPart>().Single().Text);

        MimeMessage report = PlainMessage("x");
        report.Body = new Multipart("report") { new TextPart("plain") { Text = "Delivery failed" }, new TextPart("plain") { Text = "Status: 5.1.1" } };
        Assert.Null(await ApplyAsync(report, SubmissionSource.SmartHost));
    }

    [DbFact]
    public async Task Pictures_of_the_template_travel_as_inline_parts()
    {
        await AddAsync("Logo", t => t.Html = $"<div><img src=\"data:image/png;base64,{Png}\" width=\"30\">{{{{Body}}}}</div>");
        MimeMessage message = PlainMessage("Hello");

        await ApplyAsync(message, SubmissionSource.SmartHost);

        Assert.DoesNotContain("data:image", HtmlOf(message));
        MimePart picture = Assert.Single(message.BodyParts.OfType<MimePart>(), p => p.ContentType.MimeType == "image/png");
        Assert.Contains("src=\"cid:" + picture.ContentId + "\"", HtmlOf(message));

        // alternative [ plain, related [ html, picture ] ]
        var alternative = Assert.IsAssignableFrom<Multipart>(message.Body);
        Assert.Equal("text/plain", alternative[0].ContentType.MimeType);
        var related = Assert.IsAssignableFrom<Multipart>(alternative[1]);
        Assert.True(related.ContentType.IsMimeType("multipart", "related"));
        Assert.Equal(new[] { "text/html", "image/png" }, related.Select(part => part.ContentType.MimeType));
    }

    [DbFact]
    public async Task Nothing_active_survives_in_a_template()
    {
        await AddAsync("Rogue", t => t.Html = "<script>alert(1)</script><div onclick=\"x()\">{{Body}}</div>");
        MimeMessage message = PlainMessage("Hello");

        await ApplyAsync(message, SubmissionSource.SmartHost);

        string html = HtmlOf(message);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.Contains("Hello", html);
    }

    [DbFact]
    public async Task A_message_through_the_smart_host_leaves_in_the_template_followed_by_signature_and_footer()
    {
        RelayRule printers = await AddRuleAsync("Printers");
        await AddAsync("Printers", t => { t.RelayRuleId = printers.Id; t.ForMailPrograms = false; });
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.Signatures.Add(new Signature { TenantId = _seed.Tenant.Id, Name = "Legal", Kind = SignatureKind.Footer, Html = "<p>LEGAL FOOTER</p>" });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _host.Scope();
        SubmissionResult result = await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Source = SubmissionSource.SmartHost,
            Rule = printers,
            Raw = RawMail.Build("printer@example.test", "bob@example.test", "Toner low", "Toner is low in tray 2."),
            EnvelopeFrom = "printer@example.test",
            Recipients = new[] { "bob@example.test" },
            TenantId = _seed.Tenant.Id,
        });
        Assert.True(result.Accepted, result.Error);

        var db2 = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailMessage delivered = await db2.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.BobMailbox.Id);
        byte[] raw = (await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(delivered.Id))!;
        MimeMessage mail = await MimeMessage.LoadAsync(new MemoryStream(raw));

        string html = HtmlOf(mail);
        Assert.True(html.IndexOf("<h1>Home</h1>", StringComparison.Ordinal) < html.IndexOf("Toner is low in tray 2.", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Toner is low in tray 2.", StringComparison.Ordinal) < html.IndexOf("LEGAL FOOTER", StringComparison.Ordinal));
        Assert.Contains("Toner is low in tray 2.", mail.TextBody);
        Assert.Contains("LEGAL FOOTER", mail.TextBody);
    }

    [DbFact]
    public async Task A_message_that_asks_to_go_out_unchanged_is_not_touched()
    {
        await AddAsync("Frame");
        using IServiceScope scope = _host.Scope();
        byte[] raw = RawMail.Build("alice@example.test", "bob@example.test", "As is", "No template for me");

        await scope.ServiceProvider.GetRequiredService<MailSubmission>().SubmitAsync(new SubmissionRequest
        {
            Raw = raw,
            EnvelopeFrom = "alice@example.test",
            Recipients = new[] { "bob@example.test" },
            TenantId = _seed.Tenant.Id,
            ApplyFooters = false,
        });

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailMessage delivered = await db.MailMessages.AsNoTracking().SingleAsync(m => m.MailboxId == _seed.BobMailbox.Id);
        string text = System.Text.Encoding.UTF8.GetString((await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(delivered.Id))!);
        Assert.DoesNotContain("<h1>", text);
        Assert.DoesNotContain("text/html", text);
    }
}

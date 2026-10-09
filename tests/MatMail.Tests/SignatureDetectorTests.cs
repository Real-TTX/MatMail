using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>Whether a message already carries the signature that its mail program put there.</summary>
public class SignatureDetectorTests
{
    // ---- HTML: what the programs mark their signature with ---------------------------------------------------------------

    [Theory]
    [InlineData("<pre class=\"moz-signature\" cols=\"72\">-- \nAlice Example</pre>", true)]                                   // Thunderbird, a text signature
    [InlineData("<div>Hi</div><div class=\"moz-signature\">-- <br>Alice<br>CTO</div>", true)]                              // Thunderbird, an HTML signature
    [InlineData("<div dir=\"ltr\"><div>Hi</div><div><div class=\"gmail_signature\" data-smartmail=\"gmail_signature\"><div>Alice</div></div></div></div>", true)]
    [InlineData("<div>Hi</div><div id=\"Signature\"><div>Alice Example</div></div>", true)]                                // Outlook, Outlook on the web
    [InlineData("<p>Hi</p><div id=\"_rc_sig\">Alice</div>", true)]                                                          // Roundcube
    [InlineData("<div>Hi</div><span id=\"-x-evo-signature\"><div>Alice</div></span>", true)]                                // Evolution
    [InlineData("<div>Hi</div><div class=\"mm-signature\">Alice</div>", true)]                                              // the web client of MatMail
    [InlineData("<div>Hi</div><div id=\"Signature\"><img src=\"cid:logo\"></div>", true)]                                   // a picture alone is a signature
    [InlineData("<div>Hi</div><div id=\"Signature\"></div>", false)]                                                        // the empty place a program leaves
    [InlineData("<div>Hi</div><div id=\"Signature\"> &nbsp; </div>", false)]
    [InlineData("<div>Hi</div><div>Alice</div>", false)]
    public void A_signature_is_recognised_by_the_marker_of_the_mail_program(string html, bool expected)
        => Assert.Equal(expected, SignatureDetector.HtmlCarries(html));

    [Theory]
    [InlineData("<div>Hi</div><div>Sent from my iPhone</div>", false)]
    [InlineData("<div>Hi</div><div>Gesendet von meinem iPad</div>", false)]
    [InlineData("<div>Hi</div><div id=\"ms-outlook-mobile-signature\"><div>Get Outlook for iOS</div></div>", false)]
    [InlineData("<div>Hi</div><div class=\"gmail_signature\">Sent from my Pixel</div>", false)]
    [InlineData("<div>Hi</div><div class=\"gmail_signature\">Sent from the desk of Alice Example</div>", true)]            // says something about the sender
    public void What_a_phone_or_an_app_says_about_itself_is_no_signature(string html, bool expected)
        => Assert.Equal(expected, SignatureDetector.HtmlCarries(html));

    // ---- HTML: only what the sender wrote counts, not the quoted original ----------------------------------------------

    [Theory]
    // Thunderbird, Apple Mail: a block quote
    [InlineData("<p>Thanks</p><blockquote type=\"cite\"><div class=\"moz-signature\">-- <br>Bob</div></blockquote>", false)]
    // Gmail: the quote with its attribution; the signature of the one who replies stands outside of it
    [InlineData("<div>Thanks</div><div class=\"gmail_quote\"><div class=\"gmail_attr\">On Mon, Bob wrote:</div><blockquote class=\"gmail_quote\"><div class=\"gmail_signature\">Bob</div></blockquote></div>", false)]
    [InlineData("<div>Thanks</div><div class=\"gmail_quote\"><blockquote class=\"gmail_quote\"><div class=\"gmail_signature\">Bob</div></blockquote></div><div class=\"gmail_signature\">Alice</div>", true)]
    // Thunderbird forwards
    [InlineData("<div>FYI</div><div class=\"moz-forward-container\"><div class=\"moz-signature\">Bob</div></div>", false)]
    // Outlook on the web: the signature of the one who replies comes before the marker of the quote, the original's after it
    [InlineData("<div>Thanks</div><div id=\"appendonsend\"></div><hr><div id=\"divRplyFwdMsg\"><b>From:</b> Bob</div><div id=\"Signature\">Bob</div>", false)]
    [InlineData("<div>Thanks</div><div id=\"Signature\">Alice</div><div id=\"appendonsend\"></div><hr><div id=\"divRplyFwdMsg\"><b>From:</b> Bob</div><div id=\"x_Signature\">Bob</div>", true)]
    [InlineData("<div>Thanks</div><div id=\"appendonsend\"></div><hr><div id=\"divRplyFwdMsg\"><b>From:</b> Bob</div><div><div id=\"x_Signature\">Bob</div></div>", false)]
    // Outlook for Windows: a header with a line above it, everything below is the original
    [InlineData("<html><body><div class=\"WordSection1\"><p>Thanks</p><div><div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0cm 0cm 0cm\"><p><b>From:</b> Bob</p></div></div><p>Original</p><div id=\"Signature\">Bob</div></div></body></html>", false)]
    [InlineData("<html><body><div class=\"WordSection1\"><p>Thanks</p><div id=\"Signature\">Alice</div><div><div style='border:none;border-top:solid #B5C4DF 1.0pt;padding:3.0pt 0in 0in 0in'><p><b>From:</b> Bob</p></div></div><p>Original</p></div></body></html>", true)]
    public void A_signature_inside_the_quoted_original_does_not_count(string html, bool expected)
        => Assert.Equal(expected, SignatureDetector.HtmlCarries(html));

    // ---- the separator ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<div>Hi</div><div>-- <br>Alice<br>CTO</div>", true)]
    [InlineData("<div>Hi</div><div>---</div><div>more text</div>", false)]
    [InlineData("<div>Hi</div><div>--</div>", false)]                                    // a separator with nothing below it
    public void The_separator_of_a_signature_is_found_in_html_text_too(string html, bool expected)
        => Assert.Equal(expected, SignatureDetector.HtmlCarries(html));

    [Theory]
    [InlineData("Hi\r\n\r\n-- \r\nAlice\r\nCTO\r\n", true)]
    [InlineData("Hi\n--\nAlice\n", true)]                                                 // programs that cut the blank
    [InlineData("Hi\n\n---\nAlice\n", false)]                                             // a ruler is no separator
    [InlineData("Hi\n-- \n", false)]
    [InlineData("> -- \n> Bob\n\nThanks\n", false)]                                       // quoted
    [InlineData("Thanks\n\n-- \nAlice\n\nOn Mon, Bob wrote:\n> Hi\n> -- \n> Bob\n", true)]    // above the quote
    [InlineData("On Mon, Bob wrote:\n> Hi\n\nThanks\n\n-- \nAlice\n", true)]              // below the quote
    [InlineData("Hi\n-- \nSent from my iPhone\n", false)]
    // The original below Outlook's banner or header block is not the sender's
    [InlineData("Thanks\n\n-----Original Message-----\nFrom: Bob\nSent: Monday\nSubject: Hi\n\nHi\n\n-- \nBob\n", false)]
    [InlineData("Thanks\n\nFrom: Bob <bob@example.test>\nSent: Monday, 5 October 2026 10:00\nTo: Alice\nSubject: Hi\n\nHi\n\n-- \nBob\n", false)]
    [InlineData("Danke\n\nVon: Bob <bob@example.test>\nGesendet: Montag, 5. Oktober 2026 10:00\nAn: Alice\nBetreff: Hi\n\nHi\n\n-- \nBob\n", false)]
    [InlineData("Thanks\n\n________________________________\nFrom: Bob\nSent: Monday\n\nHi\n-- \nBob\n", false)]
    [InlineData("Thanks\n\n-- \nAlice\n\n-----Original Message-----\nFrom: Bob\nSent: Monday\n\nHi\n-- \nBob\n", true)]
    public void The_separator_of_a_signature_is_found_in_plain_text(string text, bool expected)
        => Assert.Equal(expected, SignatureDetector.PlainCarries(text));

    [Fact]
    public void A_long_text_below_a_line_of_dashes_is_no_signature()
    {
        string text = "Hi\n--\n" + string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i)) + "\n";

        Assert.False(SignatureDetector.PlainCarries(text));
    }
}

/// <summary>The server adds its signature to the mail of a program only when that program has not signed.</summary>
public class SignatureDetectionApplyTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.Signatures.Add(new Signature { TenantId = _seed.Tenant.Id, Name = "Company", Kind = SignatureKind.Signature, Html = "<p>COMPANY {{DisplayName}}</p>", AddOnServer = true });
        db.Signatures.Add(new Signature { TenantId = _seed.Tenant.Id, Name = "Legal", Kind = SignatureKind.Footer, Html = "<p>LEGAL</p>" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static MimeMessage Message(string html, string text)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("someone@elsewhere.test"));
        message.Subject = "Hello";
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();
        return message;
    }

    private async Task ApplyAsync(MimeMessage message)
    {
        using IServiceScope scope = _host.Scope();
        var context = new SignatureContext("Alice Example", "alice@example.test", "CTO", null, "Home");
        await scope.ServiceProvider.GetRequiredService<SignatureService>()
            .ApplyAsync(message, SubmissionSource.MailProgram, _seed.Tenant.Id, _seed.AliceMailbox.Id, _seed.Alice.Id, context);
    }

    [DbFact]
    public async Task A_message_that_thunderbird_signed_gets_the_footer_but_no_second_signature()
    {
        MimeMessage message = Message(
            "<html><body><p>Hi</p><div class=\"moz-signature\">-- <br>Alice, Thunderbird</div></body></html>",
            "Hi\r\n\r\n-- \r\nAlice, Thunderbird\r\n");

        await ApplyAsync(message);

        Assert.DoesNotContain("COMPANY", message.HtmlBody);
        Assert.DoesNotContain("COMPANY", message.TextBody);
        Assert.Contains("LEGAL", message.HtmlBody);
        Assert.Contains("LEGAL", message.TextBody);
    }

    [DbFact]
    public async Task One_version_of_the_text_that_is_signed_is_enough_for_both()
    {
        // Outlook writes the signature into the HTML; its text version has no separator.
        MimeMessage message = Message(
            "<html><body><p>Hi</p><div id=\"Signature\"><p>Alice Example, Outlook</p></div></body></html>",
            "Hi\r\n\r\nAlice Example, Outlook\r\n");

        await ApplyAsync(message);

        Assert.DoesNotContain("COMPANY", message.HtmlBody);
        Assert.DoesNotContain("COMPANY", message.TextBody);
        Assert.Contains("LEGAL", message.TextBody);
    }

    [DbFact]
    public async Task A_reply_that_only_quotes_a_signature_is_signed()
    {
        MimeMessage message = Message(
            "<html><body><div>Thanks</div><div id=\"appendonsend\"></div><hr><div id=\"divRplyFwdMsg\"><b>From:</b> Bob</div><div id=\"x_Signature\">Bob, Outlook</div></body></html>",
            "Thanks\r\n\r\n-----Original Message-----\r\nFrom: Bob\r\n\r\nHi\r\n-- \r\nBob\r\n");

        await ApplyAsync(message);

        Assert.Contains("COMPANY Alice Example", message.HtmlBody);
        Assert.Contains("COMPANY Alice Example", message.TextBody);
    }

    [DbFact]
    public async Task A_line_that_a_phone_adds_does_not_keep_the_company_signature_away()
    {
        MimeMessage message = Message("<html><body><p>Hi</p><div>Sent from my iPhone</div></body></html>", "Hi\r\n\r\nSent from my iPhone\r\n");

        await ApplyAsync(message);

        Assert.Contains("COMPANY Alice Example", message.HtmlBody);
        Assert.Contains("COMPANY Alice Example", message.TextBody);
    }
}

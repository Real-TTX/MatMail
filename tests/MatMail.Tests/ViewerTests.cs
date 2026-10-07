using MatMail.Messaging;
using MimeKit;

namespace MatMail.Tests;

/// <summary>
/// The reader has to cope with whatever the world sends: Outlook, newsletters of T-Online and WEB.DE, Apple Mail, plain text.
/// Sample mails live in Fixtures/Mail; set MATMAIL_VIEWER_LAB_OUT to a folder to get every one of them rendered as a page there
/// (the lab script in the session scratchpad turns them into screenshots).
/// </summary>
public class ViewerTests
{
    private static readonly MailBodyRenderer Renderer = new();

    private static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mail");

    private static (RenderedBody Body, string Page) Render(string name, bool images = false)
    {
        MimeMessage message = MimeMessage.Load(Path.Combine(FixtureFolder, name + ".eml"));
        RenderedBody body = Renderer.Render(message, 7, images);
        return (body, MailBodyRenderer.BuildDocument(body));
    }

    [Fact]
    public void The_lab_writes_every_sample_mail_as_a_page_when_asked()
    {
        string? output = Environment.GetEnvironmentVariable("MATMAIL_VIEWER_LAB_OUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        foreach (string file in Directory.GetFiles(FixtureFolder, "*.eml"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(Path.Combine(output, name + ".html"), Render(name).Page);
        }
    }

    public static IEnumerable<object[]> Samples()
        => Directory.GetFiles(FixtureFolder, "*.eml").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory]
    [MemberData(nameof(Samples))]
    public void Nothing_active_survives_in_any_sample(string name)
    {
        string page = Render(name, images: true).Page;
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onclick", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", page, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("01-plain-text", "mm-plain")]
    [InlineData("09-unstyled-html", "mm-unstyled")]
    [InlineData("08-dark-text", "mm-styled")]
    [InlineData("04-tonline-newsletter", "mm-styled")]
    [InlineData("05-webde-service", "mm-styled")]
    public void A_mail_is_classified_by_who_chose_its_colours(string name, string kind)
        => Assert.Contains($"<html class=\"{kind}\">", Render(name).Page);

    [Fact]
    public void The_page_colour_of_a_newsletter_is_kept_although_the_body_element_is_gone()
    {
        string page = Render("04-tonline-newsletter").Page;
        Assert.Contains("background: rgba(242, 242, 242, 1)", page);
        Assert.DoesNotContain("<body bgcolor", page);
    }

    [Fact]
    public void Pictures_from_the_internet_wait_for_the_reader_s_consent()
    {
        (RenderedBody blocked, string page) = Render("04-tonline-newsletter");
        Assert.True(blocked.HasRemoteContent);
        Assert.DoesNotContain("static.telekom.example", page);

        (_, string allowed) = Render("04-tonline-newsletter", images: true);
        Assert.Contains("static.telekom.example/mail/hero-glasfaser.jpg", allowed);
    }

    private static (RenderedBody Body, string Page) RenderHtml(string html, bool images = false)
    {
        var message = new MimeMessage();
        message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();
        RenderedBody body = Renderer.Render(message, 7, images);
        return (body, MailBodyRenderer.BuildDocument(body));
    }

    [Fact]
    public void The_style_sheet_of_the_sender_is_kept_but_cleaned()
    {
        (_, string page) = RenderHtml(
            "<html><head><style>@import url(\"https://tracker.example/x.css\"); .h1{font-size:28px;color:#262626} .box{position:fixed;top:0;z-index:99} @media (max-width:620px){.wrap{width:100%!important}} @font-face{font-family:x;src:url(https://tracker.example/f.woff)}</style></head><body><div class=\"h1\">Hi</div></body></html>");

        Assert.Contains(".h1 { font-size: 28px", page);
        Assert.Contains("@media (max-width: 620px)", page);
        Assert.DoesNotContain("@import", page);
        Assert.DoesNotContain("@font-face", page);
        Assert.DoesNotContain("tracker.example", page);
        Assert.DoesNotContain("position: fixed", page);
        Assert.DoesNotContain("z-index", page);
    }

    [Fact]
    public void A_picture_loaded_by_a_style_sheet_waits_for_the_reader_s_consent_like_any_other()
    {
        const string html = "<html><head><style>.hero{background:url(https://tracker.example/pixel.png)}</style></head><body><div class=\"hero\">x</div></body></html>";

        (RenderedBody blocked, string blockedPage) = RenderHtml(html);
        Assert.True(blocked.HasRemoteContent);
        Assert.DoesNotContain("tracker.example", blockedPage);

        (_, string allowedPage) = RenderHtml(html, images: true);
        Assert.Contains("tracker.example/pixel.png", allowedPage);
    }

    [Fact]
    public void Links_show_where_they_lead_and_open_elsewhere()
    {
        (_, string page) = RenderHtml("<a href=\"https://example.test/path\">click</a>");
        Assert.Contains("target=\"_blank\"", page);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", page);
        Assert.Contains("title=\"https://example.test/path\"", page);
    }
}

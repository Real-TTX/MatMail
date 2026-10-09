using System.Text;
using MatMail.Messaging;
using MatMail.Tests.Support;
using MimeKit;

namespace MatMail.Tests;

/// <summary>Message files that somebody brings: an .eml as it is, an Outlook .msg turned into an ordinary message.</summary>
public class MessageFilesTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static byte[] Outlook(Action<MsgKit.Email>? change = null)
    {
        var sender = new MsgKit.Sender("max@sender.test", "Max Sender");
        using var email = new MsgKit.Email(sender, "Quarterly report", false, false, false)
        {
            BodyText = "Plain text of the report",
            BodyHtml = "<html><body><p>Hello <b>Alice</b></p><img src=\"cid:logo123\"></body></html>",
            SentOn = new DateTime(2026, 10, 5, 8, 30, 0, DateTimeKind.Utc),
            InternetMessageId = "<msg1@sender.test>",
        };
        email.Recipients.AddTo("alice@example.test", "Alice Example");
        email.Recipients.AddCc("bob@example.test", "Bob Example");
        email.Attachments.Add(new MemoryStream(Png), "logo.png", -1, true, "logo123");
        email.Attachments.Add(new MemoryStream(Encoding.UTF8.GetBytes("the numbers")), "report.txt");
        change?.Invoke(email);
        using var stream = new MemoryStream();
        email.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void An_eml_file_is_taken_as_it_is()
    {
        byte[] content = RawMail.Build("max@sender.test", "alice@example.test", "Hello", "plain");

        MessageFile file = MessageFiles.Read(content);

        Assert.Equal("eml", file.Format);
        Assert.Equal(content, file.Raw);
        Assert.Equal("Hello", file.Mime.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some text\r\nwith two lines")]
    [InlineData("<html><body>a page</body></html>")]
    public void Text_that_says_nothing_of_who_wrote_it_is_no_message(string text)
    {
        var refused = Assert.Throws<InvalidDataException>(() => MessageFiles.Read(Encoding.UTF8.GetBytes(text)));

        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
    }

    [Fact]
    public void Bytes_that_are_no_message_at_all_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => MessageFiles.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));
    }

    [Fact]
    public void An_outlook_file_becomes_an_ordinary_message_with_everything_it_had()
    {
        MessageFile file = MessageFiles.Read(Outlook());

        Assert.Equal("msg", file.Format);
        MimeMessage mime = file.Mime;
        Assert.Equal("Quarterly report", mime.Subject);
        Assert.Equal("max@sender.test", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Max Sender", mime.From.Mailboxes.Single().Name);
        Assert.Equal("alice@example.test", mime.To.Mailboxes.Single().Address);
        Assert.Equal("bob@example.test", mime.Cc.Mailboxes.Single().Address);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero), mime.Date);
        Assert.Equal("msg1@sender.test", mime.MessageId);
        Assert.Contains("Hello <b>Alice</b>", mime.HtmlBody);
        Assert.Contains("Plain text of the report", mime.TextBody);

        // the picture is a part of the text, the other file an attachment
        Assert.Equal("logo123", mime.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "logo123").ContentId);
        MimePart attachment = mime.Attachments.OfType<MimePart>().Single(a => a.FileName == "report.txt");
        using var content = new MemoryStream();
        attachment.Content!.DecodeTo(content);
        Assert.Equal("the numbers", Encoding.UTF8.GetString(content.ToArray()));

        // what is kept and shown is the message as RFC 822: it reads again as the same message
        MimeMessage again = MimeMessage.Load(new MemoryStream(file.Raw));
        Assert.Equal("Quarterly report", again.Subject);
        Assert.Equal(mime.Attachments.Count(), again.Attachments.Count());
    }

    [Fact]
    public void An_outlook_file_that_is_damaged_is_refused_with_a_reason()
    {
        byte[] content = Outlook();
        byte[] damaged = content.Take(600).ToArray();   // the start of a compound file, then nothing

        var refused = Assert.Throws<InvalidDataException>(() => MessageFiles.Read(damaged));

        Assert.Contains("Outlook", refused.Message);
    }

    [Fact]
    public void The_pictures_of_a_file_are_served_from_where_the_file_is_served()
    {
        MessageFile file = MessageFiles.Read(Outlook());

        RenderedBody body = new MailBodyRenderer().Render(file.Mime, "/api/mail/preview/0123456789abcdef0123456789abcdef", allowRemoteImages: false);

        Assert.Contains("/api/mail/preview/0123456789abcdef0123456789abcdef/cid/logo123", body.Html);
    }

    [Fact]
    public void The_pictures_of_a_message_in_a_mailbox_are_still_served_by_its_id()
    {
        MessageFile file = MessageFiles.Read(Outlook());

        RenderedBody body = new MailBodyRenderer().Render(file.Mime, 42, allowRemoteImages: false);

        Assert.Contains("/api/mail/messages/42/cid/logo123", body.Html);
    }
}

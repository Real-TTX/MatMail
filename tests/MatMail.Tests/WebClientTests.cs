using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

public class MailQueryTests
{
    [Fact]
    public void Operators_and_words_are_separated()
    {
        MailQuery query = MailQuery.Parse(1, null, "from:alice to:\"bob smith\" subject:offer has:attachment is:unread before:2026-01-31 invoice \"two words\"");

        Assert.Equal(new[] { "alice" }, query.From);
        Assert.Equal(new[] { "bob smith" }, query.To);
        Assert.Equal(new[] { "offer" }, query.Subject);
        Assert.Equal(new[] { "invoice", "two words" }, query.Words);
        Assert.True(query.HasAttachment);
        Assert.True(query.Unread);
        Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), query.Before);
        Assert.True(query.IsSearch);
    }

    [Fact]
    public void An_empty_text_is_a_plain_folder_listing()
        => Assert.False(MailQuery.Parse(1, 5, "   ").IsSearch);
}

public class MailBodyRendererTests
{
    private static MimeMessage Html(string html)
        => new() { Body = new TextPart("html") { Text = html }, Subject = "x" };

    [Fact]
    public void Scripts_handlers_and_frames_are_removed_and_links_open_elsewhere()
    {
        var renderer = new MailBodyRenderer();
        RenderedBody body = renderer.Render(Html("<p onclick=\"steal()\">Hi <a href=\"https://example.org\">link</a></p><script>alert(1)</script><iframe src=\"https://evil.test\"></iframe>"), 1, allowRemoteImages: false);

        Assert.DoesNotContain("script", body.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", body.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", body.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target=\"_blank\"", body.Html);
        Assert.Contains("noopener", body.Html);
    }

    [Fact]
    public void Pictures_from_the_internet_are_blocked_until_allowed()
    {
        var renderer = new MailBodyRenderer();
        MimeMessage message = Html("<img src=\"https://tracker.test/pixel.gif\"><div style=\"background:url(https://tracker.test/bg.png)\">x</div>");

        RenderedBody blocked = renderer.Render(message, 7, allowRemoteImages: false);
        Assert.True(blocked.HasRemoteContent);
        Assert.DoesNotContain("tracker.test", blocked.Html);

        RenderedBody allowed = renderer.Render(message, 7, allowRemoteImages: true);
        Assert.Contains("https://tracker.test/pixel.gif", allowed.Html);
    }

    [Fact]
    public void Inline_pictures_point_at_the_message_and_plain_text_becomes_html()
    {
        var renderer = new MailBodyRenderer();
        RenderedBody inline = renderer.Render(Html("<img src=\"cid:logo123\">"), 42, allowRemoteImages: false);
        Assert.Contains("/api/mail/messages/42/cid/logo123", inline.Html);

        var plain = new MimeMessage { Body = new TextPart("plain") { Text = "Line 1\nSee https://example.org <b>not bold</b>" } };
        RenderedBody text = renderer.Render(plain, 1, allowRemoteImages: false);
        Assert.True(text.WasPlainText);
        Assert.Contains("&lt;b&gt;", text.Html);
        Assert.Contains("<a", text.Html);
    }

    [Fact]
    public void Attachments_are_listed_and_inline_images_are_not()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>See <img src=\"cid:pic\"></p>", TextBody = "See attached" };
        MimeEntity picture = builder.LinkedResources.Add("pic.png", new byte[] { 1, 2, 3 }, ContentType.Parse("image/png"));
        picture.ContentId = "pic";
        builder.Attachments.Add("report.pdf", new byte[] { 1, 2, 3, 4, 5 }, ContentType.Parse("application/pdf"));
        MimeMessage message = new() { Body = builder.ToMessageBody() };

        var renderer = new MailBodyRenderer();
        AttachmentInfo attachment = Assert.Single(renderer.GetAttachments(message));
        Assert.Equal("report.pdf", attachment.FileName);
        Assert.Equal(5, attachment.Size);
        Assert.NotNull(renderer.FindByContentId(message, "<pic>"));
        Assert.NotNull(renderer.FindAttachment(message, 0));
    }
}

public class ComposeTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(c => c.Server.MaxUploadMb = 5);
        _seed = await _host.SeedAsync();
        AppInfo.DataDir = Path.Combine(Path.GetTempPath(), "matmail-test-data-" + Guid.NewGuid().ToString("N"));
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(MailUser User, IServiceScope Scope)> ScopeAsync(User user)
    {
        IServiceScope scope = _host.ScopeAs(user);
        var access = scope.ServiceProvider.GetRequiredService<MailAccessService>();
        MailUser? mailUser = await access.AuthenticateAsync(user.LoginName, "Test-Passw0rd!", "127.0.0.1");
        Assert.NotNull(mailUser);
        return (mailUser, scope);
    }

    [DbFact]
    public async Task Send_delivers_locally_saves_a_copy_and_marks_the_original_answered()
    {
        (MailUser alice, IServiceScope aliceScope) = await ScopeAsync(_seed.Alice);
        using (aliceScope)
        {
            var db = aliceScope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            var folders = aliceScope.ServiceProvider.GetRequiredService<FolderService>();
            var store = aliceScope.ServiceProvider.GetRequiredService<MailStore>();
            MailFolder inbox = (await folders.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox))!;
            MailMessage original = await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("Bob <bob@example.test>", "alice@example.test", "Lunch?", "Are you free?")));

            ComposeModel? reply = await aliceScope.ServiceProvider.GetRequiredService<ComposeService>().PrepareReplyAsync(alice, original.Id, "reply");
            Assert.NotNull(reply);
            Assert.Equal("Re: Lunch?", reply.Subject);
            Assert.Equal(new[] { "Bob <bob@example.test>" }, reply.To);
            Assert.Equal("alice@example.test", reply.From);
            Assert.Contains("Are you free?", reply.Html);
            Assert.Equal(original.Id, reply.ReplyToMessageId);

            reply.Html = "<p>Yes, at noon!</p>" + reply.Html;
            ComposeResult result = await aliceScope.ServiceProvider.GetRequiredService<ComposeService>().SendAsync(alice, reply);
            Assert.True(result.Ok, result.Error);

            db.ChangeTracker.Clear();
            Assert.True((await db.MailMessages.AsNoTracking().FirstAsync(m => m.Id == original.Id)).IsAnswered);
            Assert.Equal(1, await db.MailMessages.CountAsync(m => m.MailboxId == _seed.BobMailbox.Id && m.Subject == "Re: Lunch?"));
            Assert.Equal(1, await db.MailMessages.CountAsync(m => m.MailboxId == _seed.AliceMailbox.Id && m.Folder!.Kind == FolderKind.Sent));

            MailMessage delivered = await db.MailMessages.AsNoTracking().FirstAsync(m => m.MailboxId == _seed.BobMailbox.Id);
            Assert.Equal("<" + original.MessageIdHeader?.Trim('<', '>') + ">", delivered.InReplyTo);
            Assert.Equal(original.ThreadKey, delivered.ThreadKey);
        }
    }

    [DbFact]
    public async Task Sending_as_an_address_without_the_right_is_refused()
    {
        (MailUser alice, IServiceScope scope) = await ScopeAsync(_seed.Alice);
        using (scope)
        {
            ComposeResult result = await scope.ServiceProvider.GetRequiredService<ComposeService>().SendAsync(alice, new ComposeModel
            {
                From = "bob@example.test",
                To = { "someone@outside.test" },
                Subject = "Spoofed",
                Html = "<p>x</p>",
            });

            Assert.False(result.Ok);
        }
    }

    [DbFact]
    public async Task Drafts_are_replaced_on_every_save_and_open_with_their_attachments()
    {
        (MailUser alice, IServiceScope scope) = await ScopeAsync(_seed.Alice);
        using (scope)
        {
            var compose = scope.ServiceProvider.GetRequiredService<ComposeService>();
            var staging = scope.ServiceProvider.GetRequiredService<AttachmentStaging>();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

            StagedAttachment staged = await staging.SaveAsync(alice.UserId, "notes.txt", "text/plain", new MemoryStream("hello"u8.ToArray()), 1024);
            var model = new ComposeModel { From = "alice@example.test", To = { "bob@example.test" }, Subject = "Draft", Html = "<p>First</p>", AttachmentIds = { staged.Id } };

            ComposeResult first = await compose.SaveDraftAsync(alice, model);
            Assert.True(first.Ok);
            model.DraftId = first.DraftId;
            model.Html = "<p>Second</p>";
            ComposeResult second = await compose.SaveDraftAsync(alice, model);
            Assert.NotEqual(first.DraftId, second.DraftId);

            Assert.Equal(1, await db.MailMessages.CountAsync(m => m.IsDraft));

            ComposeModel? reopened = await compose.OpenDraftAsync(alice, second.DraftId!.Value);
            Assert.NotNull(reopened);
            Assert.Contains("Second", reopened.Html);
            Assert.Equal("notes.txt", Assert.Single(reopened.Attachments).FileName);

            await compose.DiscardDraftAsync(alice, second.DraftId.Value);
            Assert.False(await db.MailMessages.AnyAsync(m => m.IsDraft));
        }
    }

    [DbFact]
    public async Task Contact_suggestions_know_the_directory_and_past_correspondents()
    {
        (MailUser alice, IServiceScope scope) = await ScopeAsync(_seed.Alice);
        using (scope)
        {
            var store = scope.ServiceProvider.GetRequiredService<MailStore>();
            MailFolder inbox = (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox))!;
            await store.AddAsync(inbox.Id, new NewMessage(RawMail.Build("Carla Contact <carla@elsewhere.test>", "alice@example.test", "Hi", "x")));

            var contacts = scope.ServiceProvider.GetRequiredService<ContactService>();
            Assert.Contains(await contacts.SuggestAsync(alice, "bob"), c => c.Address == "bob@example.test");
            Assert.Contains(await contacts.SuggestAsync(alice, "carl"), c => c.Address == "carla@elsewhere.test");
            Assert.Empty(await contacts.SuggestAsync(alice, "nobody-like-this"));
        }
    }
}

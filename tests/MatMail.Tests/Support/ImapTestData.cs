using System.Text;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests.Support;

/// <summary>Messages, folders and rights for the IMAP tests, written through the mail core like real mail would be.</summary>
public static class ImapTestData
{
    /// <summary>A multipart/mixed message: a UTF-8 text part and a base64 PDF attachment; the sender name is RFC 2047-encoded.</summary>
    public static byte[] MultipartWithAttachment(string subject = "Bericht", string messageId = "<multi@sender.test>")
        => Encoding.UTF8.GetBytes(
            "From: =?utf-8?q?J=C3=BCrgen_M=C3=BCller?= <juergen@sender.test>\r\n" +
            "To: Alice <alice@example.test>\r\n" +
            "Cc: bob@example.test\r\n" +
            $"Subject: {subject}\r\n" +
            "Date: Tue, 07 Oct 2026 10:00:00 +0200\r\n" +
            $"Message-ID: {messageId}\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
            "\r\n" +
            "This is a multi-part message in MIME format.\r\n" +
            "--outer\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "\r\n" +
            "Hallo Alice,\r\nanbei der Bericht über Köln.\r\n" +
            "--outer\r\n" +
            "Content-Type: application/pdf; name=\"bericht.pdf\"\r\n" +
            "Content-Disposition: attachment; filename=\"bericht.pdf\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "JVBERi0xLjQK\r\n" +
            "--outer--\r\n");

    public static async Task<MailFolder> FolderAsync(TestHost host, long mailboxId, FolderKind kind)
    {
        using IServiceScope scope = host.Scope();
        return (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByKindAsync(mailboxId, kind))!;
    }

    public static async Task<MailFolder> FolderAsync(TestHost host, long mailboxId, string path)
    {
        using IServiceScope scope = host.Scope();
        return (await scope.ServiceProvider.GetRequiredService<FolderService>().FindByPathAsync(mailboxId, path))!.Folder;
    }

    public static async Task<MailFolder> CreateFolderAsync(TestHost host, long mailboxId, string path)
    {
        using IServiceScope scope = host.Scope();
        (MailFolder? folder, string? error) = await scope.ServiceProvider.GetRequiredService<FolderService>().CreateAsync(mailboxId, path);
        Assert.Null(error);
        return folder!;
    }

    /// <summary>Stores a message in a folder of a mailbox (as delivery or the web client would).</summary>
    public static async Task<MailMessage> AddAsync(TestHost host, long mailboxId, FolderKind kind, byte[] raw, Func<NewMessage, NewMessage>? shape = null)
    {
        MailFolder folder = await FolderAsync(host, mailboxId, kind);
        return await AddToFolderAsync(host, folder.Id, raw, shape);
    }

    public static async Task<MailMessage> AddToFolderAsync(TestHost host, long folderId, byte[] raw, Func<NewMessage, NewMessage>? shape = null)
    {
        using IServiceScope scope = host.Scope();
        var message = new NewMessage(raw);
        return await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(folderId, shape is null ? message : shape(message));
    }

    /// <summary>A plain message for alice@example.test with the given subject.</summary>
    public static byte[] Simple(string subject, string body = "Hello", string from = "max@sender.test", string? extraHeaders = null)
        => RawMail.Build(from, "alice@example.test", subject, body, extraHeaders: extraHeaders);

    public static async Task<MailMessage> ReloadAsync(TestHost host, long messageId)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.AsNoTracking().FirstAsync(m => m.Id == messageId);
    }

    public static async Task<List<MailMessage>> MessagesAsync(TestHost host, long folderId)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.AsNoTracking()
            .Where(m => m.FolderId == folderId).OrderBy(m => m.Uid).ToListAsync();
    }

    public static async Task<MailMessageContent> ContentAsync(TestHost host, long messageId)
    {
        using IServiceScope scope = host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessageContents.AsNoTracking().FirstAsync(c => c.MessageId == messageId);
    }

    /// <summary>Delegates a mailbox to a user with the given access.</summary>
    public static async Task GrantAsync(TestHost host, Mailbox mailbox, User user, MailboxAccess access)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailboxPermissions.Add(new MailboxPermission { TenantId = mailbox.TenantId, MailboxId = mailbox.Id, UserId = user.Id, Access = access });
        await db.SaveChangesAsync();
    }

    /// <summary>Gives a user the tenant's Administrator role (all permissions, including the "Unassigned" mailbox).</summary>
    public static async Task MakeAdministratorAsync(TestHost host, User user)
    {
        using IServiceScope scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long roleId = await db.Roles.Where(r => r.TenantId == user.TenantId && r.Name == TenantService.AdministratorRoleName).Select(r => r.Id).FirstAsync();
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    /// <summary>Delivers a message like the SMTP server or the provider sync would (routing by recipient).</summary>
    public static async Task DeliverAsync(TestHost host, string recipient, string subject)
    {
        using IServiceScope scope = host.Scope();
        await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("max@sender.test", recipient, subject, "Delivered while idling"),
            new DeliverySource { EnvelopeRecipients = new[] { recipient } });
    }
}

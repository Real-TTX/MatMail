using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Account;

/// <summary>The settings for Outlook, Thunderbird, Apple Mail and phones: this server's names, ports and the user's login.</summary>
public class MailProgramsModel(AppConfig config, MatMailDbContext db, CurrentUser currentUser, CertificateProvider certificates) : PageModel
{
    public string Host => config.Server.Hostname;
    public string LoginName => currentUser.Username ?? string.Empty;
    public string? PrimaryAddress { get; private set; }

    public bool ImapEnabled => config.Imap.Enabled;
    public int ImapTlsPort => config.Imap.ImplicitTlsPort;
    public int ImapStartTlsPort => config.Imap.Port;

    public bool SmtpEnabled => config.Smtp.Enabled;
    public int SmtpTlsPort => config.Smtp.ImplicitTlsPort;
    public int SmtpSubmissionPort => config.Smtp.SubmissionPort;

    public bool SelfSignedCertificate => certificates.Describe() is not { IsSelfSigned: false };

    public async Task OnGetAsync()
    {
        long? userId = currentUser.UserId;
        PrimaryAddress = await db.MailboxAliases.AsNoTracking()
            .Where(a => a.IsPrimary && a.Mailbox!.Type == MailboxType.Personal && a.Mailbox.OwnerUserId == userId)
            .Select(a => a.Address)
            .FirstOrDefaultAsync();
    }
}

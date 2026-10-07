using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Accounts;

public class EditModel(
    MatMailDbContext db, SecretProtector secrets, ProviderConnector connector, IAccountSyncRunner sync, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public MailAccount? Account { get; private set; }
    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> RoleItems => new[]
    {
        new SelectListItem(l["Mail – fetch and distribute"].Value, nameof(MailAccountRole.Mail)),
        new SelectListItem(l["Backup – keep a copy"].Value, nameof(MailAccountRole.Backup)),
        new SelectListItem(l["Migration – move an old account"].Value, nameof(MailAccountRole.Migration)),
        new SelectListItem(l["Send only – outgoing relay"].Value, nameof(MailAccountRole.SendOnly)),
    };

    public IReadOnlyList<SelectListItem> RetentionItems => new[]
    {
        new SelectListItem(l["Keep a copy on the server"].Value, nameof(ServerRetention.KeepOnServer)),
        new SelectListItem(l["Delete from the server after download"].Value, nameof(ServerRetention.DeleteAfterDownload)),
        new SelectListItem(l["Live access (do not store)"].Value, nameof(ServerRetention.LiveAccess)),
    };

    public IReadOnlyList<SelectListItem> ProtocolItems => new[]
    {
        new SelectListItem("IMAP", nameof(ReceiveProtocol.Imap)),
        new SelectListItem("POP3", nameof(ReceiveProtocol.Pop3)),
    };

    public IReadOnlyList<SelectListItem> SecurityItems => new[]
    {
        new SelectListItem(l["SSL/TLS (encrypted from the start)"].Value, nameof(ConnectionSecurity.Ssl)),
        new SelectListItem(l["STARTTLS (upgraded to encryption)"].Value, nameof(ConnectionSecurity.StartTls)),
        new SelectListItem(l["None (not encrypted)"].Value, nameof(ConnectionSecurity.None)),
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Role { get; set; } = nameof(MailAccountRole.Mail);
        public bool IsEnabled { get; set; } = true;
        public bool IsCatchAll { get; set; }
        public long? TargetMailboxId { get; set; }
        public string Retention { get; set; } = nameof(ServerRetention.KeepOnServer);

        public string ReceiveProtocol { get; set; } = nameof(Data.ReceiveProtocol.Imap);
        public string? ReceiveHost { get; set; }
        public int ReceivePort { get; set; } = 993;
        public string ReceiveSecurity { get; set; } = nameof(ConnectionSecurity.Ssl);
        public string? ReceiveUsername { get; set; }
        public string? ReceivePassword { get; set; }
        public bool AllowInvalidCertificate { get; set; }

        public string? SendHost { get; set; }
        public int SendPort { get; set; } = 587;
        public string SendSecurity { get; set; } = nameof(ConnectionSecurity.StartTls);
        public bool SendUsesReceiveCredentials { get; set; } = true;
        public string? SendUsername { get; set; }
        public string? SendPassword { get; set; }

        public int SyncIntervalMinutes { get; set; } = 5;
        public bool SyncAllFolders { get; set; }
        public string? SyncFolders { get; set; }
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        Account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id);
        if (Account is null)
        {
            return NotFound();
        }

        Input = ToInput(Account);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        MailAccount? existing = null;
        if (IsEdit)
        {
            existing = await db.MailAccounts.FirstOrDefaultAsync(a => a.Id == Id);
            if (existing is null)
            {
                return NotFound();
            }

            Account = existing;
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        MailAccount account = existing ?? new MailAccount();
        var source = (account.ReceiveHost, account.ReceiveUsername, account.ReceiveProtocol, account.Role);
        Apply(account);
        if (existing is null)
        {
            db.MailAccounts.Add(account);
        }

        await db.SaveChangesAsync();
        if (existing is not null && source != (account.ReceiveHost, account.ReceiveUsername, account.ReceiveProtocol, account.Role))
        {
            // The UIDs of another server or mailbox mean other messages: forget what was fetched so far.
            await sync.ResetAsync(account.Id);
        }

        this.Notify(l[IsEdit ? "The account was saved." : "The account was created. Test the connection and start the synchronisation."].Value);
        return RedirectToPage("Index");
    }

    /// <summary>Tries the connection with what is in the form (nothing is saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        await LoadListsAsync();
        MailAccount? existing = IsEdit ? await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id) : null;
        Account = existing;

        Validate(forTest: true);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new MailAccount { TenantId = existing?.TenantId ?? 0 };
        Apply(probe, existing);

        var lines = new List<string>();
        bool ok = true;
        if (probe.Role != MailAccountRole.SendOnly)
        {
            ConnectionTestResult receive = await connector.TestReceiveAsync(probe, HttpContext.RequestAborted);
            ok &= receive.Ok;
            lines.Add(receive.Message);
        }

        if (!string.IsNullOrWhiteSpace(probe.SendHost))
        {
            ConnectionTestResult send = await connector.TestSendAsync(probe, HttpContext.RequestAborted);
            ok &= send.Ok;
            lines.Add(send.Message);
        }

        this.NotifyNow(ok, string.Join(" ", lines));
        return Page();
    }

    public async Task<IActionResult> OnPostSyncNowAsync()
    {
        await LoadListsAsync();
        Account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id);
        if (Account is null)
        {
            return NotFound();
        }

        Input = ToInput(Account);
        SyncOutcome outcome = await sync.SyncNowAsync(Id, HttpContext.RequestAborted);
        Account = await db.MailAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id);
        this.NotifyNow(outcome.Ok, outcome.Message);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.MailAccounts.Where(a => a.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The account was deleted."].Value);
        return RedirectToPage("Index");
    }

    // ---------------------------------------------------------------------------------------------------------------

    private void Validate(bool forTest = false)
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }

        if (!MailAddresses.IsValid(Input.Address))
        {
            ModelState.AddModelError("Input.Address", l["The e-mail address is not valid."]);
        }

        if (!Enum.TryParse(Input.Role, out MailAccountRole role))
        {
            role = MailAccountRole.Mail;
        }

        bool receives = role != MailAccountRole.SendOnly;
        if (receives)
        {
            if (string.IsNullOrWhiteSpace(Input.ReceiveHost))
            {
                ModelState.AddModelError("Input.ReceiveHost", l["The server is required."]);
            }

            if (string.IsNullOrWhiteSpace(Input.ReceiveUsername))
            {
                ModelState.AddModelError("Input.ReceiveUsername", l["The user name is required."]);
            }

            if (Input.ReceivePort is < 1 or > 65535)
            {
                ModelState.AddModelError("Input.ReceivePort", l["The port must be between 1 and 65535."]);
            }

            if (!forTest && string.IsNullOrEmpty(Input.ReceivePassword) && !HasStoredReceivePassword())
            {
                ModelState.AddModelError("Input.ReceivePassword", l["The password is required."]);
            }

            if (!Enum.TryParse(Input.Retention, out ServerRetention retention))
            {
                retention = ServerRetention.KeepOnServer;
            }

            if (retention == ServerRetention.LiveAccess && (Input.ReceiveProtocol != nameof(Data.ReceiveProtocol.Imap) || role != MailAccountRole.Mail))
            {
                ModelState.AddModelError("Input.Retention", l["Live access works with IMAP and the role Mail only."]);
            }

            if (role == MailAccountRole.Backup && retention == ServerRetention.DeleteAfterDownload)
            {
                ModelState.AddModelError("Input.Retention", l["A backup never deletes mail at the provider."]);
            }

            if (role is MailAccountRole.Backup or MailAccountRole.Migration && Input.TargetMailboxId is null)
            {
                ModelState.AddModelError("Input.TargetMailboxId", l["Choose the mailbox that receives the copies."]);
            }

            if (Input.SyncIntervalMinutes is < 1 or > 1440)
            {
                ModelState.AddModelError("Input.SyncIntervalMinutes", l["The interval must be between 1 and 1440 minutes."]);
            }
        }

        bool sendCredentialsNeeded = role == MailAccountRole.SendOnly || !Input.SendUsesReceiveCredentials;
        if (role == MailAccountRole.SendOnly && string.IsNullOrWhiteSpace(Input.SendHost))
        {
            ModelState.AddModelError("Input.SendHost", l["The server is required."]);
        }

        if (!string.IsNullOrWhiteSpace(Input.SendHost))
        {
            if (Input.SendPort is < 1 or > 65535)
            {
                ModelState.AddModelError("Input.SendPort", l["The port must be between 1 and 65535."]);
            }

            if (sendCredentialsNeeded && string.IsNullOrWhiteSpace(Input.SendUsername))
            {
                ModelState.AddModelError("Input.SendUsername", l["The user name is required."]);
            }
        }

        if (Input.TargetMailboxId is long mailboxId && !MailboxItems.Any(m => m.Value == mailboxId.ToString()))
        {
            ModelState.AddModelError("Input.TargetMailboxId", l["The mailbox does not exist."]);
        }
    }

    private bool HasStoredReceivePassword() => Account is not null && !string.IsNullOrEmpty(Account.ReceivePasswordProtected);

    /// <summary>Copies the form into the entity. Empty password fields keep the stored (encrypted) passwords.</summary>
    private void Apply(MailAccount account, MailAccount? passwordSource = null)
    {
        passwordSource ??= Account;
        Enum.TryParse(Input.Role, out MailAccountRole role);
        account.Name = Input.Name.Trim();
        account.Address = MailAddresses.Normalize(Input.Address);
        account.Role = role;
        account.IsEnabled = Input.IsEnabled;
        account.IsCatchAll = Input.IsCatchAll && role != MailAccountRole.SendOnly;
        account.TargetMailboxId = role == MailAccountRole.SendOnly ? null : Input.TargetMailboxId;
        account.AllowInvalidCertificate = Input.AllowInvalidCertificate;
        account.Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();

        if (role == MailAccountRole.SendOnly)
        {
            account.ReceiveProtocol = Data.ReceiveProtocol.None;
            account.ReceiveHost = null;
            account.ReceiveUsername = null;
            account.ReceivePasswordProtected = null;
            account.Retention = ServerRetention.KeepOnServer;
            account.SyncAllFolders = false;
        }
        else
        {
            Enum.TryParse(Input.ReceiveProtocol, out ReceiveProtocol protocol);
            Enum.TryParse(Input.ReceiveSecurity, out ConnectionSecurity security);
            Enum.TryParse(Input.Retention, out ServerRetention retention);
            account.ReceiveProtocol = protocol == Data.ReceiveProtocol.None ? Data.ReceiveProtocol.Imap : protocol;
            account.ReceiveHost = Input.ReceiveHost?.Trim();
            account.ReceivePort = Input.ReceivePort;
            account.ReceiveSecurity = security;
            account.ReceiveUsername = Input.ReceiveUsername?.Trim();
            account.ReceivePasswordProtected = string.IsNullOrEmpty(Input.ReceivePassword) ? passwordSource?.ReceivePasswordProtected : secrets.Protect(Input.ReceivePassword);
            account.Retention = role == MailAccountRole.Backup ? ServerRetention.KeepOnServer : retention;
            account.SyncAllFolders = role is MailAccountRole.Backup or MailAccountRole.Migration || Input.SyncAllFolders;
            account.SyncIntervalMinutes = Math.Clamp(Input.SyncIntervalMinutes, 1, 1440);
        }

        Enum.TryParse(Input.SendSecurity, out ConnectionSecurity sendSecurity);
        bool sends = !string.IsNullOrWhiteSpace(Input.SendHost);
        account.SendHost = sends ? Input.SendHost!.Trim() : null;
        account.SendPort = Input.SendPort;
        account.SendSecurity = sendSecurity;
        account.SendUsesReceiveCredentials = role != MailAccountRole.SendOnly && Input.SendUsesReceiveCredentials;
        account.SendUsername = account.SendUsesReceiveCredentials || !sends ? null : Input.SendUsername?.Trim();
        account.SendPasswordProtected = account.SendUsesReceiveCredentials || !sends
            ? null
            : string.IsNullOrEmpty(Input.SendPassword) ? passwordSource?.SendPasswordProtected : secrets.Protect(Input.SendPassword);

        account.SyncFolders = (Input.SyncFolders ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (account.SyncFolders.Length == 0)
        {
            account.SyncFolders = new[] { "INBOX" };
        }
    }

    private InputModel ToInput(MailAccount a) => new()
    {
        Name = a.Name,
        Address = a.Address,
        Role = a.Role.ToString(),
        IsEnabled = a.IsEnabled,
        IsCatchAll = a.IsCatchAll,
        TargetMailboxId = a.TargetMailboxId,
        Retention = a.Retention.ToString(),
        ReceiveProtocol = a.ReceiveProtocol == Data.ReceiveProtocol.None ? nameof(Data.ReceiveProtocol.Imap) : a.ReceiveProtocol.ToString(),
        ReceiveHost = a.ReceiveHost,
        ReceivePort = a.ReceivePort,
        ReceiveSecurity = a.ReceiveSecurity.ToString(),
        ReceiveUsername = a.ReceiveUsername,
        AllowInvalidCertificate = a.AllowInvalidCertificate,
        SendHost = a.SendHost,
        SendPort = a.SendPort,
        SendSecurity = a.SendSecurity.ToString(),
        SendUsesReceiveCredentials = a.SendUsesReceiveCredentials,
        SendUsername = a.SendUsername,
        SyncIntervalMinutes = a.SyncIntervalMinutes,
        SyncAllFolders = a.SyncAllFolders,
        SyncFolders = string.Join('\n', a.SyncFolders),
        Notes = a.Notes,
    };

    private async Task LoadListsAsync()
    {
        MailboxItems = await db.Mailboxes.AsNoTracking()
            .Where(m => m.Type != MailboxType.Unassigned && m.IsActive)
            .OrderBy(m => m.Name)
            .Select(m => new SelectListItem(m.Name, m.Id.ToString()))
            .ToListAsync();
    }
}

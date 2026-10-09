using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

public class TargetModel(MatMailDbContext db, BackupService backups, SecretProtector secrets, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public BackupTarget? Target { get; private set; }
    public IReadOnlyList<SelectListItem> KindItems { get; private set; } = Array.Empty<SelectListItem>();
    public bool HasStoredPassword => !string.IsNullOrEmpty(Target?.PasswordProtected);

    /// <summary>The folder of the data volume that backups may go to (shown as help).</summary>
    public string DataBackupFolder => Path.Combine(config.DataDir, "backups");

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = nameof(BackupTargetKind.Local);
        public bool IsActive { get; set; } = true;
        public string? LocalFolder { get; set; }
        public string? Host { get; set; }
        public string? Share { get; set; }
        public string? ShareFolder { get; set; }
        public string? Domain { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        LoadLists();
        if (!IsEdit)
        {
            Input.LocalFolder = DataBackupFolder;
            Input.ShareFolder = "matmail";
            return Page();
        }

        Target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id);
        if (Target is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = Target.Name,
            Kind = Target.Kind.ToString(),
            IsActive = Target.IsActive,
            LocalFolder = Target.Kind == BackupTargetKind.Local ? Target.Path : null,
            Host = Target.Host,
            Share = Target.Share,
            ShareFolder = Target.Kind == BackupTargetKind.Smb ? Target.Path : null,
            Domain = Target.Domain,
            Username = Target.Username,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        LoadLists();
        Target = IsEdit ? await db.BackupTargets.FirstOrDefaultAsync(t => t.Id == Id) : null;
        if (IsEdit && Target is null)
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        BackupTarget target = Target ?? new BackupTarget();
        if (Target is null)
        {
            db.BackupTargets.Add(target);
        }

        Apply(target);
        await db.SaveChangesAsync();

        // saved: say at once whether it works
        string? password = string.IsNullOrEmpty(Input.Password) ? null : Input.Password;
        StorageCheck check = await backups.CheckAsync(target, password, HttpContext.RequestAborted);
        target.LastCheckDate = DateTime.UtcNow;
        target.LastCheckOk = check.Ok;
        target.LastCheckMessage = check.Ok ? null : check.Message;
        await db.SaveChangesAsync();

        this.Notify(
            check.Ok ? l["The target was saved. {0}"].Value.Replace("{0}", check.Message) : l["The target was saved, but it cannot be used yet: {0}"].Value.Replace("{0}", check.Message),
            check.Ok ? NoticeKind.Ok : NoticeKind.Warn);
        return RedirectToPage("Targets");
    }

    /// <summary>Tries what is in the form (nothing is saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        LoadLists();
        Target = IsEdit ? await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id) : null;
        Validate(forTest: true);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new BackupTarget { PasswordProtected = Target?.PasswordProtected };
        Apply(probe);
        StorageCheck check = await backups.CheckAsync(probe, string.IsNullOrEmpty(Input.Password) ? null : Input.Password, HttpContext.RequestAborted);
        string free = check.FreeBytes is long bytes ? " " + string.Format(l["{0} free."].Value, Fmt.Size(bytes)) : string.Empty;
        this.NotifyNow(check.Ok, check.Message + (check.Ok ? free : string.Empty));
        return Page();
    }

    /// <summary>Lists the shares the server offers (for the field "Share").</summary>
    public async Task<IActionResult> OnPostSharesAsync()
    {
        LoadLists();
        Target = IsEdit ? await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id) : null;
        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", l["The server is required."]);
            return Page();
        }

        string? password = string.IsNullOrEmpty(Input.Password) ? secrets.Unprotect(Target?.PasswordProtected) : Input.Password;
        try
        {
            IReadOnlyList<string> shares = await SmbBackupStorage.ListSharesAsync(Input.Host.Trim(), Input.Domain, Input.Username, password, HttpContext.RequestAborted);
            this.NotifyNow(shares.Count > 0 ? string.Format(l["Shares of {0}: {1}"].Value, Input.Host.Trim(), string.Join(", ", shares)) : l["The server offers no shares."].Value, shares.Count > 0 ? NoticeKind.Ok : NoticeKind.Warn);
        }
        catch (BackupStorageException ex)
        {
            this.NotifyNow(false, ex.Message);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int plans = await db.BackupPlans.CountAsync(p => p.TargetId == Id);
        if (plans > 0)
        {
            this.Notify(string.Format(l["The target is used by {0} schedule(s). Delete or change those first."].Value, plans), NoticeKind.Danger);
            return RedirectToPage(new { id = Id });
        }

        int deleted = await db.BackupTargets.Where(t => t.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The target was deleted. The backups in it are still there."].Value);
        return RedirectToPage("Targets");
    }

    private void Apply(BackupTarget target)
    {
        target.Name = Input.Name.Trim();
        target.IsActive = Input.IsActive;
        bool smb = Input.Kind == nameof(BackupTargetKind.Smb);
        target.Kind = smb ? BackupTargetKind.Smb : BackupTargetKind.Local;
        if (!smb)
        {
            target.Path = (Input.LocalFolder ?? string.Empty).Trim();
            target.Host = target.Share = target.Domain = target.Username = target.PasswordProtected = null;
            return;
        }

        target.Path = (Input.ShareFolder ?? string.Empty).Trim().Trim('/', '\\');
        target.Host = Input.Host?.Trim();
        target.Share = Input.Share?.Trim().Trim('/', '\\');
        target.Domain = string.IsNullOrWhiteSpace(Input.Domain) ? null : Input.Domain.Trim();
        target.Username = string.IsNullOrWhiteSpace(Input.Username) ? null : Input.Username.Trim();
        if (!string.IsNullOrEmpty(Input.Password))
        {
            target.PasswordProtected = secrets.Protect(Input.Password);
        }
    }

    private void Validate(bool forTest = false)
    {
        if (!forTest)
        {
            if (string.IsNullOrWhiteSpace(Input.Name))
            {
                ModelState.AddModelError("Input.Name", l["Name is required."]);
            }
            else if (db.BackupTargets.Any(t => t.Name == Input.Name.Trim() && t.Id != Id))
            {
                ModelState.AddModelError("Input.Name", l["There is a target of that name already."]);
            }
        }

        if (Input.Kind == nameof(BackupTargetKind.Smb))
        {
            if (string.IsNullOrWhiteSpace(Input.Host) || Input.Host.Trim().IndexOfAny([' ', '/', '\\']) >= 0)
            {
                ModelState.AddModelError("Input.Host", l["Enter the name or the address of the server, e.g. nas.local."]);
            }

            if (string.IsNullOrWhiteSpace(Input.Share) || Input.Share.Trim().Trim('/', '\\').IndexOfAny(['/', '\\']) >= 0)
            {
                ModelState.AddModelError("Input.Share", l["Enter the name of the share, e.g. backups."]);
            }

            if ((Input.ShareFolder ?? string.Empty).Split('/', '\\').Any(s => s is ".." or "."))
            {
                ModelState.AddModelError("Input.ShareFolder", l["The folder must not contain “..”."]);
            }

            if (!string.IsNullOrWhiteSpace(Input.Username) && string.IsNullOrEmpty(Input.Password) && string.IsNullOrEmpty(Target?.PasswordProtected))
            {
                ModelState.AddModelError("Input.Password", l["The password is required."]);
            }

            return;
        }

        string? problem = LocalTargetPolicy.Check(Input.LocalFolder, config.DataDir) switch
        {
            LocalTargetProblem.Missing => l["The folder is missing."].Value,
            LocalTargetProblem.NotAbsolute => l["The folder must be a full path, e.g. /backups."].Value,
            LocalTargetProblem.Dots => l["The folder must not contain “..”."].Value,
            LocalTargetProblem.InsideDataVolume => string.Format(l["Inside the data volume only {0} may be used (anywhere else the backups would end up in the next backup)."].Value, DataBackupFolder),
            _ => null,
        };
        if (problem is not null)
        {
            ModelState.AddModelError("Input.LocalFolder", problem);
        }
    }

    private void LoadLists()
        => KindItems =
        [
            new SelectListItem(l["Folder of this server"].Value, nameof(BackupTargetKind.Local)),
            new SelectListItem(l["Network share (SMB)"].Value, nameof(BackupTargetKind.Smb)),
        ];
}

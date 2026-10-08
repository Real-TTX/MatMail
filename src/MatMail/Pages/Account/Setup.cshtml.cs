using MatMail.Backup;
using MatMail.Data;
using MatMail.Services;
using MatMail.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

/// <summary>The first start: create the first administrator, or restore the backup of another installation instead.</summary>
[DisableRequestSizeLimit]
public class SetupModel(
    SetupService setup,
    SignInService signIn,
    MatMailDbContext db,
    RestorePreparation preparation,
    BackupService backups,
    IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new() { TenantName = "Home" };

    /// <summary>The id of a backup that was uploaded to restore it instead of setting up.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Upload { get; set; }

    [BindProperty]
    public RestoreInput Restore { get; set; } = new();

    public class RestoreInput
    {
        public string? Passphrase { get; set; }
    }

    public BackupInfo? UploadInfo { get; private set; }
    public bool UploadEncrypted { get; private set; }
    public string? UploadError { get; private set; }
    public string? UploadProblem { get; private set; }
    public bool HasUpload { get; private set; }
    public PreparationStatus Preparation => preparation.Status;
    public IReadOnlyDictionary<string, string> Texts => Pages.Admin.Backup.BackupTexts.For(l);

    public class InputModel
    {
        public string TenantName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public string? MailAddress { get; set; }
        public string Password { get; set; } = string.Empty;
        public string PasswordRepeat { get; set; } = string.Empty;
    }

    public void OnGet() => LoadUpload(passphrase: null);

    /// <summary>A backup file as the body of the request (see the restore page of the administration).</summary>
    public async Task<IActionResult> OnPostUploadAsync(string? name, CancellationToken cancel)
    {
        try
        {
            (Guid id, long bytes, bool encrypted) = await preparation.SaveUploadAsync(Request.Body, name, cancel);
            return new JsonResult(new { id = id.ToString("N"), bytes, encrypted });
        }
        catch (BackupCorruptException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public IActionResult OnGetStatus() => Pages.Admin.Backup.BackupStatus.Json(backups, preparation, downloads: null, token: null);

    public IActionResult OnPostCheck()
    {
        LoadUpload(Restore.Passphrase);
        return Page();
    }

    public IActionResult OnPostRestore()
    {
        LoadUpload(Restore.Passphrase);
        if (!HasUpload || UploadProblem is not null)
        {
            return Page();
        }

        if (UploadEncrypted && string.IsNullOrEmpty(Restore.Passphrase))
        {
            ModelState.AddModelError("Restore.Passphrase", l["The backup is encrypted: enter its passphrase."]);
            return Page();
        }

        // an empty installation has nothing to save first; the outgoing mail of the backup is held like always
        var request = new RestoreRequest(null, null, Guid.ParseExact(Upload!, "N"), string.IsNullOrEmpty(Restore.Passphrase) ? null : Restore.Passphrase, SafetyBackup: false, HoldOutboundQueue: true, VerifyFirst: true, "setup");
        if (!preparation.Start(request))
        {
            ModelState.AddModelError(string.Empty, l["Something else is running. Wait until it is done."]);
            return Page();
        }

        return RedirectToPage(new { Upload });
    }

    public IActionResult OnPostDiscard()
    {
        if (Guid.TryParseExact(Upload, "N", out Guid id))
        {
            preparation.DiscardUpload(id);
        }

        preparation.Reset();
        return RedirectToPage();
    }

    public IActionResult OnPostReset()
    {
        preparation.Reset();
        return RedirectToPage(new { Upload });
    }

    private void LoadUpload(string? passphrase)
    {
        if (string.IsNullOrEmpty(Upload))
        {
            return;
        }

        if (!Guid.TryParseExact(Upload, "N", out Guid id) || preparation.FindUpload(id) is not { } path)
        {
            UploadError = l["The uploaded file is not there any more. Upload it again."].Value;
            return;
        }

        HasUpload = true;
        UploadEncrypted = BackupFiles.IsEncrypted(path);
        if (UploadEncrypted && string.IsNullOrEmpty(passphrase))
        {
            return;
        }

        try
        {
            using BackupArchive archive = BackupArchive.Open(path, passphrase);
            UploadInfo = archive.Describe();
            UploadProblem = VersionGuard.WhyNotRestorable(
                archive.Manifest.Database.SchemaVersion,
                archive.Manifest.Database.DataVersion,
                archive.Manifest.Format,
                BackupFormat.Version,
                db.Database.GetMigrations(),
                DataMigrations.Latest);
        }
        catch (Exception ex) when (ex is BackupPassphraseException or BackupCorruptException)
        {
            UploadError = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.TenantName))
        {
            ModelState.AddModelError("Input.TenantName", l["Name is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.DisplayName))
        {
            ModelState.AddModelError("Input.DisplayName", l["Name is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.LoginName))
        {
            ModelState.AddModelError("Input.LoginName", l["Login name is required."]);
        }

        if (!string.IsNullOrWhiteSpace(Input.MailAddress) && !MailAddresses.IsValid(Input.MailAddress))
        {
            ModelState.AddModelError("Input.MailAddress", l["The e-mail address is not valid."]);
        }

        string? passwordError = SignInService.ValidatePasswordStrength(Input.Password);
        if (passwordError is not null)
        {
            ModelState.AddModelError("Input.Password", l[passwordError]);
        }
        else if (Input.Password != Input.PasswordRepeat)
        {
            ModelState.AddModelError("Input.PasswordRepeat", l["The passwords do not match."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        string? error = await setup.CreateFirstAdministratorAsync(Input.TenantName, Input.DisplayName, Input.LoginName, Input.MailAddress, Input.Password);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, l[error]);
            return Page();
        }

        User user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.LoginName == SignInService.NormalizeLoginName(Input.LoginName));
        await signIn.SignInAsync(user);
        return Redirect("/");
    }
}

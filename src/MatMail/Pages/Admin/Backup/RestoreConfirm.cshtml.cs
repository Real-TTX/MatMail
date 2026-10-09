using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using MatMail.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

/// <summary>What a backup holds, and the question whether it really is to replace what is here.</summary>
public class RestoreConfirmModel(
    MatMailDbContext db,
    BackupStorageFactory storages,
    RestorePreparation preparation,
    CurrentUser currentUser,
    IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long? Target { get; set; }

    [BindProperty(Name = "file", SupportsGet = true)]
    public string? FileName { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Upload { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public string? Passphrase { get; set; }
        public bool SafetyBackup { get; set; } = true;
        public bool HoldQueue { get; set; } = true;
        public bool VerifyFirst { get; set; } = true;
        public bool Understand { get; set; }
    }

    public string FileLabel { get; private set; } = string.Empty;
    public BackupFileName? Parsed { get; private set; }
    public bool Encrypted { get; private set; }
    public long? Bytes { get; private set; }
    public BackupInfo? Info { get; private set; }

    /// <summary>Why this version cannot restore it (null: it can).</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the backup could not be opened (a wrong passphrase, a damaged file).</summary>
    public string? Error { get; private set; }

    public bool SourceOk { get; private set; }
    public PreparationStatus Preparation => preparation.Status;
    public IReadOnlyDictionary<string, string> Texts => BackupTexts.For(l);

    /// <summary>The file on this server, when there is one to open right now (an upload, or a file in a folder of the server).</summary>
    private string? _localPath;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancel)
    {
        await LoadSourceAsync(cancel);
        if (SourceOk && Preparation.State == PreparationState.Idle)
        {
            Describe(passphrase: null);
        }

        return Page();
    }

    /// <summary>Opens the backup with the passphrase that was typed and says what it holds.</summary>
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken cancel)
    {
        await LoadSourceAsync(cancel);
        if (SourceOk)
        {
            Describe(Input.Passphrase);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancel)
    {
        await LoadSourceAsync(cancel);
        if (!SourceOk)
        {
            return Page();
        }

        if (!Input.Understand)
        {
            ModelState.AddModelError("Input.Understand", l["Confirm that the current data is replaced."]);
            Describe(Input.Passphrase);
            return Page();
        }

        if (Encrypted && string.IsNullOrEmpty(Input.Passphrase))
        {
            ModelState.AddModelError("Input.Passphrase", l["The backup is encrypted: enter its passphrase."]);
            return Page();
        }

        Guid? upload = Guid.TryParseExact(Upload, "N", out Guid id) ? id : null;
        var request = new RestoreRequest(
            upload is null ? Target : null,
            upload is null ? FileName : null,
            upload,
            string.IsNullOrEmpty(Input.Passphrase) ? null : Input.Passphrase,
            Input.SafetyBackup,
            Input.HoldQueue,
            Input.VerifyFirst,
            currentUser.DisplayName ?? currentUser.Username ?? "?");
        if (!preparation.Start(request))
        {
            this.Notify(l["Something else is running (a backup or another restore). Wait until it is done."].Value, NoticeKind.Warn);
        }

        return RedirectToPage(new { target = Target, file = FileName, upload = Upload });
    }

    /// <summary>Forgets a failed preparation (it was shown) and shows the question again.</summary>
    public IActionResult OnPostReset()
    {
        preparation.Reset();
        return RedirectToPage(new { target = Target, file = FileName, upload = Upload });
    }

    private async Task LoadSourceAsync(CancellationToken cancel)
    {
        if (!string.IsNullOrEmpty(Upload))
        {
            if (!Guid.TryParseExact(Upload, "N", out Guid id) || preparation.FindUpload(id) is not { } path)
            {
                Error = l["The uploaded file is not there any more. Upload it again."].Value;
                return;
            }

            _localPath = path;
            FileLabel = l["The uploaded file"].Value;
            Encrypted = BackupFiles.IsEncrypted(path);
            Bytes = new FileInfo(path).Length;
            SourceOk = true;
            return;
        }

        if (Target is not long targetId || string.IsNullOrEmpty(FileName) || !BackupFiles.IsBackupFileName(FileName))
        {
            Error = l["No backup was chosen."].Value;
            return;
        }

        BackupTarget? target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId, cancel);
        if (target is null)
        {
            Error = l["The target does not exist any more."].Value;
            return;
        }

        FileLabel = FileName;
        Parsed = BackupFiles.Parse(FileName);
        Encrypted = Parsed?.Encrypted ?? false;
        try
        {
            using IBackupStorage storage = storages.Create(target);
            RemoteBackupFile? found = (await storage.ListAsync(cancel)).FirstOrDefault(f => f.Name == FileName);
            if (found is null)
            {
                Error = string.Format(l["“{0}” is not in the target “{1}” (any more)."].Value, FileName, target.Name);
                return;
            }

            Bytes = found.Bytes;
            if (storage.LocalFolder is { } folder)
            {
                _localPath = Path.Combine(folder, FileName);
            }

            SourceOk = true;
        }
        catch (BackupStorageException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Opens the backup (when it is here to open and its passphrase is known) and compares its versions with this program's.</summary>
    private void Describe(string? passphrase)
    {
        if (_localPath is null || (Encrypted && string.IsNullOrEmpty(passphrase)))
        {
            return;
        }

        try
        {
            using BackupArchive archive = BackupArchive.Open(_localPath, passphrase);
            Info = archive.Describe();
            Problem = VersionGuard.WhyNotRestorable(
                archive.Manifest.Database.SchemaVersion,
                archive.Manifest.Database.DataVersion,
                archive.Manifest.Format,
                BackupFormat.Version,
                db.Database.GetMigrations(),
                DataMigrations.Latest);
        }
        catch (BackupPassphraseException ex)
        {
            Error = ex.Message;
        }
        catch (BackupCorruptException ex)
        {
            Error = ex.Message;
        }
    }
}

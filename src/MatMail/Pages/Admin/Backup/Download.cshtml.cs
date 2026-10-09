using MatMail.Backup;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

/// <summary>A backup made now, to be downloaded (to keep a copy somewhere else, or to move to another server).</summary>
public class DownloadModel(BackupDownloads downloads, BackupService backups, RestorePreparation preparation, IStringLocalizer<SharedResource> l) : PageModel
{
    public const int MinPassphraseLength = 8;

    [BindProperty(SupportsGet = true)]
    public Guid? Token { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public string? Passphrase { get; set; }
        public string? PassphraseRepeat { get; set; }
    }

    public DownloadStatus? Status { get; private set; }
    public IReadOnlyDictionary<string, string> Texts => BackupTexts.For(l);

    public void OnGet() => Status = Token is Guid token ? downloads.Get(token) : null;

    public IActionResult OnPostStart()
    {
        string? passphrase = string.IsNullOrEmpty(Input.Passphrase) ? null : Input.Passphrase;
        if (passphrase is not null && passphrase.Length < MinPassphraseLength)
        {
            ModelState.AddModelError("Input.Passphrase", string.Format(l["The passphrase needs at least {0} characters."].Value, MinPassphraseLength));
        }
        else if (passphrase is not null && passphrase != Input.PassphraseRepeat)
        {
            ModelState.AddModelError("Input.PassphraseRepeat", l["The two passphrases are not the same."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Guid? token = downloads.Start(passphrase);
        if (token is null)
        {
            this.Notify(l["Another backup is running. Wait until it is done."].Value, NoticeKind.Warn);
            return RedirectToPage();
        }

        return RedirectToPage(new { token });
    }

    public IActionResult OnGetStatus(Guid token) => BackupStatus.Json(backups, preparation, downloads, token);

    public IActionResult OnGetFile(Guid token)
    {
        if (downloads.Ready(token) is not { } ready)
        {
            return NotFound();
        }

        return PhysicalFile(ready.Path, "application/octet-stream", ready.Name);
    }

    public IActionResult OnPostRemove(Guid token)
    {
        downloads.Remove(token);
        return RedirectToPage("Index");
    }
}

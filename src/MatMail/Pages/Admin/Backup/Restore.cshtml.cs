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

/// <summary>One backup file of a target as the restore page lists it.</summary>
public sealed record BackupFileRow(string Name, long Bytes, DateTime CreatedUtc, string MadeBy, string Version, bool Encrypted);

/// <summary>Where a restore starts: a backup in one of the targets, or a file that is uploaded.</summary>
[DisableRequestSizeLimit]
public class RestoreModel(MatMailDbContext db, BackupStorageFactory storages, RestorePreparation preparation, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    private const int MaxRows = 100;

    [BindProperty(SupportsGet = true)]
    public long? Target { get; set; }

    public IReadOnlyList<SelectListItem> TargetItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<BackupFileRow> Files { get; private set; } = Array.Empty<BackupFileRow>();
    public int TotalFiles { get; private set; }
    public string? TargetError { get; private set; }
    public PreparationStatus Preparation => preparation.Status;

    public IReadOnlyDictionary<string, string> Texts => BackupTexts.For(l);

    public async Task OnGetAsync(CancellationToken cancel)
    {
        TargetItems = await db.BackupTargets.AsNoTracking().OrderBy(t => t.Name).Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToListAsync(cancel);
        if (Target is null && TargetItems.Count == 1)
        {
            Target = long.Parse(TargetItems[0].Value);
        }

        if (Target is not long targetId)
        {
            return;
        }

        BackupTarget? target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId, cancel);
        if (target is null)
        {
            return;
        }

        try
        {
            using IBackupStorage storage = storages.Create(target);
            Dictionary<string, string> plans = await db.BackupPlans.AsNoTracking().ToDictionaryAsync(p => "p" + p.Id, p => p.Name, cancel);
            var rows = new List<BackupFileRow>();
            foreach (RemoteBackupFile file in await storage.ListAsync(cancel))
            {
                BackupFileName? name = BackupFiles.Parse(file.Name);
                if (name is null)
                {
                    continue;
                }

                string madeBy = name.Label switch
                {
                    BackupFiles.ManualLabel => l["By hand"].Value,
                    BackupFiles.PreRestoreLabel => l["Before a restore"].Value,
                    _ => plans.GetValueOrDefault(name.Label) ?? l["A schedule that is gone"].Value,
                };
                rows.Add(new BackupFileRow(file.Name, file.Bytes, name.CreatedUtc, madeBy, name.Version, name.Encrypted));
            }

            TotalFiles = rows.Count;
            Files = rows.OrderByDescending(r => r.CreatedUtc).Take(MaxRows).ToList();
        }
        catch (BackupStorageException ex)
        {
            TargetError = ex.Message;
        }
    }

    /// <summary>A backup of a target to the browser (read from the target while it is sent).</summary>
    public async Task<IActionResult> OnGetFileAsync(long target, string file, CancellationToken cancel)
    {
        BackupTarget? entity = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == target, cancel);
        if (entity is null || !BackupFiles.IsBackupFileName(file))
        {
            return NotFound();
        }

        try
        {
            IBackupStorage storage = storages.Create(entity);
            Stream stream = await storage.OpenReadAsync(file, cancel);
            HttpContext.Response.RegisterForDispose(storage);
            return File(stream, "application/octet-stream", file);
        }
        catch (BackupStorageException ex)
        {
            this.Notify(ex.Message, NoticeKind.Danger);
            return RedirectToPage(new { target });
        }
    }

    /// <summary>A backup file, sent as the raw body of the request. The answer holds the id the confirmation page asks for.</summary>
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

    public string UploadFolder => PendingRestore.IncomingFolder(config.DataDir);
}

using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

/// <summary>What is going on with the backups: the run that is going now, how the last restore ended, warnings, and the history.</summary>
public class IndexModel(MatMailDbContext db, BackupService backups, RestorePreparation preparation, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "date_desc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<BackupRun> Paged { get; private set; } = new(Array.Empty<BackupRun>(), 0, 1, 1, Pager.DefaultPageSize);
    public RestoreReport? Report { get; private set; }
    public BackupActivity? Activity { get; private set; }
    public IReadOnlyList<SelectListItem> PlanItems { get; private set; } = Array.Empty<SelectListItem>();
    public List<(NoticeKind Kind, string Text)> Warnings { get; } = [];
    public DateTime? LastSuccess { get; private set; }

    /// <summary>The texts the page script shows (handed over as JSON).</summary>
    public IReadOnlyDictionary<string, string> Texts => BackupTexts.For(l);

    public async Task OnGetAsync()
    {
        Report = RestoreReport.Read(config.DataDir);
        Activity = backups.Coordinator.Current;

        PlanItems = await db.BackupPlans.AsNoTracking().Where(p => p.Target!.IsActive).OrderBy(p => p.Name)
            .Select(p => new SelectListItem(p.Name + " → " + p.Target!.Name, p.Id.ToString())).ToListAsync();
        await LoadWarningsAsync();

        IQueryable<BackupRun> query = db.BackupRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(r => EF.Functions.ILike(r.PlanName, pattern) || EF.Functions.ILike(r.TargetName, pattern) || (r.FileName != null && EF.Functions.ILike(r.FileName, pattern)));
        }

        query = Status switch
        {
            "succeeded" => query.Where(r => r.Status == BackupRunStatus.Succeeded),
            "failed" => query.Where(r => r.Status == BackupRunStatus.Failed),
            _ => query,
        };

        query = Sort switch
        {
            "date_asc" => query.OrderBy(r => r.StartedDate),
            "size_desc" => query.OrderByDescending(r => r.Bytes),
            _ => query.OrderByDescending(r => r.StartedDate),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }

    private async Task LoadWarningsAsync()
    {
        List<BackupPlan> plans = await db.BackupPlans.AsNoTracking().Include(p => p.Target).ToListAsync();
        LastSuccess = await db.BackupRuns.AsNoTracking().Where(r => r.Status == BackupRunStatus.Succeeded).MaxAsync(r => (DateTime?)r.StartedDate);

        if (plans.Count == 0)
        {
            Warnings.Add((NoticeKind.Warn, l["No backup is scheduled. Add a target and a schedule so that the data is saved regularly."].Value));
            return;
        }

        if (!plans.Any(p => p.IsActive && p.Target!.IsActive))
        {
            Warnings.Add((NoticeKind.Warn, l["All schedules are switched off: nothing is backed up."].Value));
        }

        foreach (BackupPlan plan in plans.Where(p => p.IsActive && p.LastStatus == BackupRunStatus.Failed))
        {
            BackupRun? failed = await db.BackupRuns.AsNoTracking().Where(r => r.PlanId == plan.Id).OrderByDescending(r => r.StartedDate).FirstOrDefaultAsync();
            Warnings.Add((NoticeKind.Danger, string.Format(l["The last backup of “{0}” failed: {1}"].Value, plan.Name, failed?.Message ?? "?")));
        }

        if (config.Backup.Enabled is false)
        {
            Warnings.Add((NoticeKind.Warn, l["Scheduled backups are switched off in the configuration (Backup:Enabled)."].Value));
        }
        else if (LastSuccess is null || LastSuccess < DateTime.UtcNow.AddDays(-2))
        {
            Warnings.Add((NoticeKind.Warn, LastSuccess is null ? l["There is no successful backup yet."].Value : string.Format(l["The last successful backup is from {0}."].Value, LastSuccess.Value.ToString("g"))));
        }
    }

    /// <summary>Polled by the page while something runs.</summary>
    public IActionResult OnGetStatus() => BackupStatus.Json(backups, preparation, downloads: null, token: null);

    public IActionResult OnPostRun(long planId)
    {
        if (backups.StartInBackground(planId, BackupRunKind.Manual))
        {
            this.Notify(l["The backup was started."].Value, NoticeKind.Info);
        }
        else
        {
            this.Notify(l["Another backup is running. Wait until it is done."].Value, NoticeKind.Warn);
        }

        return RedirectToPage();
    }

    public IActionResult OnPostCancel()
    {
        backups.Coordinator.Cancel();
        return RedirectToPage();
    }

    public IActionResult OnPostDismiss()
    {
        RestoreReport.Clear(config.DataDir);
        return RedirectToPage();
    }
}

/// <summary>What the script of the backup pages asks for: the run that is going on, the preparation of a restore, a backup made for download.</summary>
public static class BackupStatus
{
    public static JsonResult Json(BackupService backups, RestorePreparation preparation, BackupDownloads? downloads, Guid? token)
    {
        PreparationStatus prep = preparation.Status;
        DownloadStatus? download = token is Guid id ? downloads?.Get(id) : null;
        return new JsonResult(new
        {
            activity = backups.Coordinator.Current,
            preparation = new { state = prep.State.ToString(), message = prep.Message },
            download = download is null ? null : new { state = download.State.ToString(), fileName = download.FileName, bytes = download.Bytes, message = download.Message },
        });
    }
}

/// <summary>The texts of the backup pages' script, by key.</summary>
public static class BackupTexts
{
    public static IReadOnlyDictionary<string, string> For(IStringLocalizer<SharedResource> l) => new Dictionary<string, string>
    {
        ["start"] = l["Getting ready …"],
        ["database"] = l["Saving the database"],
        ["files"] = l["Saving the files"],
        ["verify"] = l["Checking the backup"],
        ["upload"] = l["Sending the backup to the target"],
        ["fetch"] = l["Fetching the backup from the target"],
        ["done"] = l["Done"],
        ["running"] = l["A backup is running"],
        ["restoring"] = l["The restore is being prepared"],
        ["restarting"] = l["MatMail restarts now and restores the backup. This page leaves for the progress page by itself. If nothing happens for a minute, start MatMail again (a container needs a restart policy such as “unless-stopped”)."],
        ["failed"] = l["The restore could not be prepared."],
        ["uploading"] = l["Uploading …"],
        ["uploadFailed"] = l["The upload failed."],
        ["ready"] = l["The backup is ready."],
        ["bytes"] = l["{0} so far"],
    };
}

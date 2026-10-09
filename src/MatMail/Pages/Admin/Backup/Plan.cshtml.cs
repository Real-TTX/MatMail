using System.Globalization;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

public class PlanModel(MatMailDbContext db, BackupService backups, SecretProtector secrets, IStringLocalizer<SharedResource> l) : PageModel
{
    public const int MinPassphraseLength = 8;

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public BackupPlan? Plan { get; private set; }
    public IReadOnlyList<SelectListItem> TargetItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> FrequencyItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> WeekdayItems { get; private set; } = Array.Empty<SelectListItem>();
    public bool HasStoredPassphrase => Plan?.Encrypt == true && !string.IsNullOrEmpty(Plan.PassphraseProtected);

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public long? TargetId { get; set; }
        public string Frequency { get; set; } = nameof(BackupFrequency.Daily);
        public int EveryHours { get; set; } = 6;
        public string Time { get; set; } = "03:00";
        public int Weekday { get; set; } = 1;
        public int DayOfMonth { get; set; } = 1;
        public int KeepLast { get; set; } = 7;
        public int KeepDaily { get; set; }
        public int KeepWeekly { get; set; } = 4;
        public int KeepMonthly { get; set; } = 6;
        public bool Encrypt { get; set; }
        public string? Passphrase { get; set; }
        public string? PassphraseRepeat { get; set; }
        public bool Verify { get; set; } = true;
        public bool NotifyOnFailure { get; set; } = true;
        public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        Plan = await db.BackupPlans.AsNoTracking().Include(p => p.Target).FirstOrDefaultAsync(p => p.Id == Id);
        if (Plan is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = Plan.Name,
            TargetId = Plan.TargetId,
            Frequency = Plan.Frequency.ToString(),
            EveryHours = Plan.EveryHours,
            Time = TimeSpan.FromMinutes(Plan.MinuteOfDay).ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            Weekday = Plan.DayOfWeek,
            DayOfMonth = Plan.DayOfMonth,
            KeepLast = Plan.KeepLast,
            KeepDaily = Plan.KeepDaily,
            KeepWeekly = Plan.KeepWeekly,
            KeepMonthly = Plan.KeepMonthly,
            Encrypt = Plan.Encrypt,
            Verify = Plan.Verify,
            NotifyOnFailure = Plan.NotifyOnFailure,
            IsActive = Plan.IsActive,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        Plan = IsEdit ? await db.BackupPlans.Include(p => p.Target).FirstOrDefaultAsync(p => p.Id == Id) : null;
        if (IsEdit && Plan is null)
        {
            return NotFound();
        }

        (int minuteOfDay, BackupFrequency frequency) = Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        BackupPlan plan = Plan ?? new BackupPlan();
        if (Plan is null)
        {
            db.BackupPlans.Add(plan);
        }

        plan.Name = Input.Name.Trim();
        plan.TargetId = Input.TargetId!.Value;
        plan.Frequency = frequency;
        plan.EveryHours = Input.EveryHours;
        plan.MinuteOfDay = minuteOfDay;
        plan.DayOfWeek = Input.Weekday;
        plan.DayOfMonth = Input.DayOfMonth;
        plan.KeepLast = Input.KeepLast;
        plan.KeepDaily = Input.KeepDaily;
        plan.KeepWeekly = Input.KeepWeekly;
        plan.KeepMonthly = Input.KeepMonthly;
        plan.Verify = Input.Verify;
        plan.NotifyOnFailure = Input.NotifyOnFailure;
        plan.IsActive = Input.IsActive;
        plan.Encrypt = Input.Encrypt;
        if (!Input.Encrypt)
        {
            plan.PassphraseProtected = null;
        }
        else if (!string.IsNullOrEmpty(Input.Passphrase))
        {
            plan.PassphraseProtected = secrets.Protect(Input.Passphrase);
        }

        // a changed schedule starts from now: the next time is the first one after this moment
        plan.NextRunDate = BackupSchedule.NextRunUtc(plan, DateTime.UtcNow, backups.Zone);
        plan.ConsecutiveFailures = 0;
        await db.SaveChangesAsync();

        this.Notify(l[IsEdit ? "The schedule was saved." : "The schedule was created."].Value);
        return RedirectToPage("Plans");
    }

    /// <summary>Starts the plan now (what is saved, not what is typed).</summary>
    public IActionResult OnPostRunNow()
    {
        if (backups.StartInBackground(Id, BackupRunKind.Manual))
        {
            this.Notify(l["The backup was started."].Value, NoticeKind.Info);
            return RedirectToPage("Index");
        }

        this.Notify(l["Another backup is running. Wait until it is done."].Value, NoticeKind.Warn);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.BackupPlans.Where(p => p.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The schedule was deleted. The backups it made are still where they are."].Value);
        return RedirectToPage("Plans");
    }

    private (int MinuteOfDay, BackupFrequency Frequency) Validate()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }
        else if (db.BackupPlans.Any(p => p.Name == Input.Name.Trim() && p.Id != Id))
        {
            ModelState.AddModelError("Input.Name", l["There is a schedule of that name already."]);
        }

        if (Input.TargetId is not long targetId || !TargetItems.Any(t => t.Value == targetId.ToString()))
        {
            ModelState.AddModelError("Input.TargetId", l["Choose where the backups go."]);
        }

        if (!Enum.TryParse(Input.Frequency, out BackupFrequency frequency))
        {
            frequency = BackupFrequency.Daily;
            ModelState.AddModelError("Input.Frequency", l["Choose how often."]);
        }

        int minuteOfDay = 0;
        if (!TimeSpan.TryParseExact(Input.Time ?? string.Empty, @"h\:mm", CultureInfo.InvariantCulture, out TimeSpan time) && !TimeSpan.TryParse(Input.Time, CultureInfo.InvariantCulture, out time))
        {
            ModelState.AddModelError("Input.Time", l["Enter a time like 03:00."]);
        }
        else
        {
            minuteOfDay = (int)Math.Clamp(time.TotalMinutes, 0, 1439);
        }

        if (frequency == BackupFrequency.Hourly && Input.EveryHours is < 1 or > 12)
        {
            ModelState.AddModelError("Input.EveryHours", l["Between 1 and 12 hours."]);
        }

        if (frequency == BackupFrequency.Weekly && Input.Weekday is < 0 or > 6)
        {
            ModelState.AddModelError("Input.Weekday", l["Choose a day of the week."]);
        }

        if (frequency == BackupFrequency.Monthly && Input.DayOfMonth is < 1 or > 28)
        {
            ModelState.AddModelError("Input.DayOfMonth", l["Between 1 and 28 (so that every month has the day)."]);
        }

        if (Input.KeepLast is < 1 or > 1000)
        {
            ModelState.AddModelError("Input.KeepLast", l["Between 1 and 1000."]);
        }

        foreach ((string field, int value) in new[] { ("Input.KeepDaily", Input.KeepDaily), ("Input.KeepWeekly", Input.KeepWeekly), ("Input.KeepMonthly", Input.KeepMonthly) })
        {
            if (value is < 0 or > 1000)
            {
                ModelState.AddModelError(field, l["Between 0 and 1000."]);
            }
        }

        if (Input.Encrypt)
        {
            bool keeps = HasStoredPassphrase && string.IsNullOrEmpty(Input.Passphrase);
            if (!keeps && (Input.Passphrase ?? string.Empty).Length < MinPassphraseLength)
            {
                ModelState.AddModelError("Input.Passphrase", string.Format(l["The passphrase needs at least {0} characters."].Value, MinPassphraseLength));
            }
            else if (!keeps && Input.Passphrase != Input.PassphraseRepeat)
            {
                ModelState.AddModelError("Input.PassphraseRepeat", l["The two passphrases are not the same."]);
            }
        }

        return (minuteOfDay, frequency);
    }

    private async Task LoadListsAsync()
    {
        TargetItems = await db.BackupTargets.AsNoTracking().OrderBy(t => t.Name).Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToListAsync();
        FrequencyItems =
        [
            new SelectListItem(l["Every few hours"].Value, nameof(BackupFrequency.Hourly)),
            new SelectListItem(l["Every day"].Value, nameof(BackupFrequency.Daily)),
            new SelectListItem(l["Every week"].Value, nameof(BackupFrequency.Weekly)),
            new SelectListItem(l["Every month"].Value, nameof(BackupFrequency.Monthly)),
        ];
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        WeekdayItems = Enumerable.Range(0, 7).Select(day => new SelectListItem(format.GetDayName((DayOfWeek)day), day.ToString())).ToList();
    }
}

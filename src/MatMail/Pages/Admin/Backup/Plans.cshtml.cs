using System.Globalization;
using MatMail.Backup;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Backup;

/// <summary>The schedules: what is backed up when, where to, and for how long it is kept.</summary>
public class PlansModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name_asc";

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public PageResult<BackupPlan> Paged { get; private set; } = new(Array.Empty<BackupPlan>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        IQueryable<BackupPlan> query = db.BackupPlans.AsNoTracking().Include(p => p.Target);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string pattern = Pager.ContainsPattern(Search);
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Target!.Name, pattern));
        }

        query = Status switch
        {
            "enabled" => query.Where(p => p.IsActive),
            "disabled" => query.Where(p => !p.IsActive),
            "failed" => query.Where(p => p.LastStatus == BackupRunStatus.Failed),
            _ => query,
        };

        query = Sort switch
        {
            "name_desc" => query.OrderByDescending(p => p.Name),
            "next_asc" => query.OrderBy(p => p.NextRunDate == null).ThenBy(p => p.NextRunDate),
            _ => query.OrderBy(p => p.Name),
        };

        Paged = await query.ToPageAsync(PageNumber);
        PageNumber = Paged.PageNumber;
    }

    public string Schedule(BackupPlan plan) => BackupDescriptions.Schedule(plan, l);

    public string Keeps(BackupPlan plan) => BackupDescriptions.Keeps(plan, l);
}

/// <summary>The schedule and the retention of a plan in words.</summary>
public static class BackupDescriptions
{
    public static string Schedule(BackupPlan plan, IStringLocalizer<SharedResource> l)
    {
        string time = TimeSpan.FromMinutes(Math.Clamp(plan.MinuteOfDay, 0, 1439)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return plan.Frequency switch
        {
            BackupFrequency.Hourly => plan.EveryHours == 1
                ? string.Format(l["Every hour at minute {0}"].Value, plan.MinuteOfDay % 60)
                : string.Format(l["Every {0} hours (at minute {1})"].Value, plan.EveryHours, plan.MinuteOfDay % 60),
            BackupFrequency.Weekly => string.Format(l["Every {0} at {1}"].Value, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)(((plan.DayOfWeek % 7) + 7) % 7)), time),
            BackupFrequency.Monthly => string.Format(l["Monthly on day {0} at {1}"].Value, plan.DayOfMonth, time),
            _ => string.Format(l["Every day at {0}"].Value, time),
        };
    }

    public static string Keeps(BackupPlan plan, IStringLocalizer<SharedResource> l)
    {
        var parts = new List<string> { string.Format(l["the last {0}"].Value, plan.KeepLast) };
        if (plan.KeepDaily > 0)
        {
            parts.Add(string.Format(l["one per day for {0} days"].Value, plan.KeepDaily));
        }

        if (plan.KeepWeekly > 0)
        {
            parts.Add(string.Format(l["one per week for {0} weeks"].Value, plan.KeepWeekly));
        }

        if (plan.KeepMonthly > 0)
        {
            parts.Add(string.Format(l["one per month for {0} months"].Value, plan.KeepMonthly));
        }

        return string.Join(", ", parts);
    }
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class AppearanceModel(MatMailDbContext db, CurrentUser currentUser, SessionCache cache, ThemeService themes, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public string Mode { get; set; } = "system";
        public string Accent { get; set; } = "blue";
        public string Culture { get; set; } = string.Empty;
        public string TextSize { get; set; } = "normal";
        public string Density { get; set; } = "comfortable";
        public string? TimeZone { get; set; }
        public bool ShowPreviews { get; set; } = true;
    }

    /// <summary>The time zones of this server, west to east, with their offset right now.</summary>
    public IReadOnlyList<SelectListItem> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => (zone, offset: zone.GetUtcOffset(DateTime.UtcNow)))
        .OrderBy(entry => entry.offset).ThenBy(entry => entry.zone.Id, StringComparer.Ordinal)
        .Select(entry => new SelectListItem($"{entry.zone.Id} (UTC{OffsetText(entry.offset)})", entry.zone.Id))
        .ToList();

    private static string OffsetText(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString("00") + ":" + Math.Abs(offset.Minutes).ToString("00");

    public IReadOnlyList<SelectListItem> Cultures => new[]
    {
        new SelectListItem(l["Browser setting / default"].Value, string.Empty),
        new SelectListItem("Deutsch", "de-DE"),
        new SelectListItem("English", "en-US"),
    };

    public void OnGet()
    {
        ThemeChoice theme = themes.Resolve();
        Input = new InputModel
        {
            Mode = theme.Mode,
            Accent = theme.Accent,
            Culture = User.FindFirst(AppClaims.Culture)?.Value ?? string.Empty,
            TextSize = theme.TextSize,
            Density = theme.Density,
            TimeZone = theme.UserTimeZone,
            ShowPreviews = theme.ShowPreviews,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        User? user = await LoadUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        user.ThemeMode = ThemeService.Modes.Contains(Input.Mode) ? Input.Mode : "system";
        user.ThemeAccent = ThemeService.Accents.Contains(Input.Accent) ? Input.Accent : "blue";
        user.Culture = Cultures.Any(c => c.Value == Input.Culture && c.Value.Length > 0) ? Input.Culture : null;
        user.TextSize = ThemeService.TextSizes.Contains(Input.TextSize) ? Input.TextSize : "normal";
        user.Density = ThemeService.Densities.Contains(Input.Density) ? Input.Density : "comfortable";
        user.TimeZone = TimeZones.Any(z => z.Value == Input.TimeZone) && Fmt.IsKnownZone(Input.TimeZone) ? Input.TimeZone : null;
        user.ShowPreviews = Input.ShowPreviews;
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);

        this.Notify(l["Your appearance settings were saved."].Value);
        return RedirectToPage();
    }

    /// <summary>The quick theme switch in the account menu posts here (fetch, no redirect).</summary>
    public async Task<IActionResult> OnPostModeAsync(string mode)
    {
        User? user = await LoadUserAsync();
        if (user is null || !ThemeService.Modes.Contains(mode))
        {
            return BadRequest();
        }

        user.ThemeMode = mode;
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);
        return new OkResult();
    }

    private Task<User?> LoadUserAsync()
    {
        long? id = currentUser.UserId;
        return db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id);
    }
}

using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class AppearanceModel(MatMailDbContext db, CurrentUser currentUser, SessionCache cache, ThemeService themes, BrandingService branding, AppConfig config, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public string Mode { get; set; } = "system";
        public string Accent { get; set; } = "default";
        public string Culture { get; set; } = string.Empty;
        public string TextSize { get; set; } = "normal";
        public string Density { get; set; } = "comfortable";
        public string? TimeZone { get; set; }
        public bool ShowPreviews { get; set; } = true;
        public string ReadingPane { get; set; } = "off";
        public bool ConversationView { get; set; }
    }

    /// <summary>The time zones of this server, west to east, with their offset right now.</summary>
    public IReadOnlyList<SelectListItem> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => (zone, offset: zone.GetUtcOffset(DateTime.UtcNow)))
        .OrderBy(entry => entry.offset).ThenBy(entry => entry.zone.Id, StringComparer.Ordinal)
        .Select(entry => new SelectListItem($"{entry.zone.Id} (UTC{OffsetText(entry.offset)})", entry.zone.Id))
        .ToList();

    private static string OffsetText(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString("00") + ":" + Math.Abs(offset.Minutes).ToString("00");

    public IReadOnlyList<(string Key, string Label)> AccentChoices => ThemeService.AccentChoices(l);

    public IReadOnlyList<SelectListItem> Cultures => new[]
    {
        new SelectListItem(l["Browser setting / default"].Value, string.Empty),
        new SelectListItem("Deutsch", "de-DE"),
        new SelectListItem("English", "en-US"),
    };

    /// <summary>The colour of the tenant (#rrggbb), or null: what the choice "Default" looks like.</summary>
    public string? TenantColour { get; private set; }

    /// <summary>The preset the administrator made the default of the installation, or null for the colour of the app.</summary>
    public string? DefaultPreset => ThemeService.NormalizeAccent(config.Display.ThemeAccent) is { } preset && preset != "default" ? preset : null;

    public async Task OnGetAsync()
    {
        TenantColour = (await branding.GetAsync(currentUser.TenantId)).AccentColor;
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
            ReadingPane = theme.ReadingPane,
            ConversationView = theme.ConversationView,
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
        user.ThemeAccent = ThemeService.NormalizeAccent(Input.Accent) ?? "default";
        user.Culture = Cultures.Any(c => c.Value == Input.Culture && c.Value.Length > 0) ? Input.Culture : null;
        user.TextSize = ThemeService.TextSizes.Contains(Input.TextSize) ? Input.TextSize : "normal";
        user.Density = ThemeService.Densities.Contains(Input.Density) ? Input.Density : "comfortable";
        user.TimeZone = TimeZones.Any(z => z.Value == Input.TimeZone) && Fmt.IsKnownZone(Input.TimeZone) ? Input.TimeZone : null;
        user.ShowPreviews = Input.ShowPreviews;
        user.ReadingPane = Input.ReadingPane is "right" or "below" ? Input.ReadingPane : null;
        user.ConversationView = Input.ConversationView;
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);

        this.Notify(l["Your appearance settings were saved."].Value);
        return RedirectToPage();
    }

    /// <summary>The theme button of the menu (the toolkit posts <c>theme</c> and the token with fetch) saves here, without a redirect.</summary>
    public async Task<IActionResult> OnPostModeAsync(string theme)
    {
        User? user = await LoadUserAsync();
        if (user is null || !ThemeService.Modes.Contains(theme))
        {
            return BadRequest();
        }

        user.ThemeMode = theme;
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

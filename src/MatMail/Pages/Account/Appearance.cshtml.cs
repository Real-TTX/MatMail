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
    }

    public IReadOnlyList<SelectListItem> Cultures => new[]
    {
        new SelectListItem(l["Browser setting / default"].Value, string.Empty),
        new SelectListItem("Deutsch", "de-DE"),
        new SelectListItem("English", "en-US"),
    };

    public void OnGet()
    {
        ThemeChoice theme = themes.Resolve();
        Input = new InputModel { Mode = theme.Mode, Accent = theme.Accent, Culture = User.FindFirst(AppClaims.Culture)?.Value ?? string.Empty };
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

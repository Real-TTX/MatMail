using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Pages.Account;

/// <summary>The language switch (DE / EN): remembers the choice in a cookie and, when signed in, in the profile.</summary>
public class LanguageModel(MatMailDbContext db, CurrentUser currentUser, SessionCache cache) : PageModel
{
    private static readonly string[] Supported = { "de-DE", "en-US" };

    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync(string culture, string? returnUrl)
    {
        if (Supported.Contains(culture))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });

            if (currentUser.UserId is long id)
            {
                User? user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id);
                if (user is not null)
                {
                    user.Culture = culture;
                    await db.SaveChangesAsync();
                    cache.InvalidateUser(id);
                }
            }
        }

        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}

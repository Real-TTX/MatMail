using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

public class SessionsModel(MatMailDbContext db, CurrentUser currentUser, SignInService signIn, SessionCache cache, IStringLocalizer<SharedResource> l) : PageModel
{
    public IReadOnlyList<UserSession> Sessions { get; private set; } = Array.Empty<UserSession>();
    public Guid CurrentToken { get; private set; }

    public async Task OnGetAsync()
    {
        Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token);
        CurrentToken = token;
        long? userId = currentUser.UserId;
        Sessions = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresDate > DateTime.UtcNow)
            .OrderByDescending(s => s.LastSeenDate)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid token)
    {
        long? userId = currentUser.UserId;
        if (await db.UserSessions.AnyAsync(s => s.Token == token && s.UserId == userId))
        {
            await signIn.RevokeAsync(token);
            this.Notify(l["The session was ended."].Value);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeOthersAsync()
    {
        Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid keep);
        long? userId = currentUser.UserId;
        await db.UserSessions.Where(s => s.UserId == userId && s.Token != keep).ExecuteDeleteAsync();
        cache.InvalidateUser(userId!.Value);
        this.Notify(l["All other sessions were ended."].Value);
        return RedirectToPage();
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatMail.Services;

/// <summary>
/// Validates the session cookie against the <c>UserSession</c> table on every request and rebuilds the principal from the
/// current database state (roles, tenant, theme), so permission changes and revoked sessions take effect right away.
/// </summary>
public sealed class SessionCookieEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (!Guid.TryParse(context.Principal?.FindFirstValue(AppClaims.SessionToken), out Guid token))
        {
            await RejectAsync(context);
            return;
        }

        IServiceProvider services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<SessionCache>();

        if (!cache.TryGet(token, out SessionSnapshot? snapshot))
        {
            snapshot = await services.GetRequiredService<SignInService>().LoadSnapshotAsync(token);
            if (snapshot is null)
            {
                cache.Invalidate(token);
                await RejectAsync(context);
                return;
            }

            cache.Set(token, snapshot);
        }

        context.ReplacePrincipal(SignInService.BuildPrincipal(snapshot, token));
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

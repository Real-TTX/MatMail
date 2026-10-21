using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatMail.Services;

/// <summary>
/// Validates the session cookie against the <c>UserSession</c> table on every request and rebuilds the principal from the
/// current database state (roles, tenant, theme), so permission changes and revoked sessions take effect right away.
/// Calls of the API (<c>/api/...</c>, or a script's <c>X-Requested-With: XMLHttpRequest</c>) get 401 / 403 instead of the redirect to the sign-in
/// page (the web client reacts to 401). This has to live here: with <c>EventsType</c> set the handler uses this class and ignores <c>options.Events</c>.
/// </summary>
public sealed class SessionCookieEvents : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        => IsApi(context.Request) ? Answer(context.Response, StatusCodes.Status401Unauthorized) : base.RedirectToLogin(context);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        => IsApi(context.Request) ? Answer(context.Response, StatusCodes.Status403Forbidden) : base.RedirectToAccessDenied(context);

    private static bool IsApi(HttpRequest request)
        => request.Path.StartsWithSegments("/api") || string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private static Task Answer(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }

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

using System.Security.Claims;

namespace MatMail.Services;

/// <summary>
/// Two-factor authentication can be required (by the policy of the tenant or by a role). Whoever is bound to it but has not set it
/// up yet may only open the page where that happens, sign out, and fetch what pages need to be drawn: everything else leads there.
/// A forced password change comes first (the password page lets the user through, so the two never lead in circles).
/// </summary>
public sealed class TwoFactorSetupMiddleware
{
    public const string SecurityPath = "/Account/Security";

    private static readonly string[] StaticPrefixes = { "/css/", "/js/", "/icons/", "/brand/", "/favicon" };
    // "/Error" is where a refused request (the 403 of the mail API) is re-executed to be answered; it shows nothing but the error.
    private static readonly string[] AllowedPages = { "/Account/Logout", "/Account/Language", "/healthz", "/Error" };

    private readonly RequestDelegate _next;

    public TwoFactorSetupMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        ClaimsPrincipal user = context.User;
        bool mustSetUp = user.Identity?.IsAuthenticated == true
            && user.FindFirstValue(AppClaims.TwoFactorRequired) == "1"
            && user.FindFirstValue(AppClaims.TwoFactor) != "1"
            && user.FindFirstValue(AppClaims.MustChangePassword) != "1";

        if (mustSetUp && !IsAllowed(context.Request.Path))
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Redirect(SecurityPath);
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// A fixed list, not "anything with a file extension": the mail API has addresses that end in a file name (attachments,
    /// inline images), and those must stay closed.
    /// </summary>
    private static bool IsAllowed(PathString path)
    {
        string value = path.Value ?? string.Empty;
        return value.Equals(SecurityPath, StringComparison.OrdinalIgnoreCase)
            || AllowedPages.Any(page => value.StartsWith(page, StringComparison.OrdinalIgnoreCase))
            || StaticPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

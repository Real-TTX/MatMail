using System.Security.Claims;

namespace MatMail.Services;

/// <summary>An administrator can force a password change: until it happened, everything leads to the password page.</summary>
public sealed class MustChangePasswordMiddleware
{
    public const string PasswordPath = "/Account/Password";

    private readonly RequestDelegate _next;

    public MustChangePasswordMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirstValue(AppClaims.MustChangePassword) == "1"
            && !IsAllowed(context.Request.Path))
        {
            context.Response.Redirect(PasswordPath);
            return;
        }

        await _next(context);
    }

    private static bool IsAllowed(PathString path)
    {
        string value = path.Value ?? string.Empty;
        return value.Equals(PasswordPath, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/Account/Language", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase)
            || Path.HasExtension(value);
    }
}

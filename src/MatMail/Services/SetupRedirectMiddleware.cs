namespace MatMail.Services;

/// <summary>Remembers whether the first administrator exists, so the check is a database hit only until it does.</summary>
public sealed class SetupState
{
    public volatile bool HasUsers;
}

/// <summary>
/// While the installation has no user at all, every page leads to the setup page (which creates the first tenant and
/// the first administrator). Afterwards the setup page is closed for good.
/// </summary>
public sealed class SetupRedirectMiddleware
{
    public const string SetupPath = "/Account/Setup";

    private readonly RequestDelegate _next;
    private readonly SetupState _state;

    public SetupRedirectMiddleware(RequestDelegate next, SetupState state)
    {
        _next = next;
        _state = state;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        if (IsStaticOrInternal(path))
        {
            await _next(context);
            return;
        }

        if (!_state.HasUsers && await context.RequestServices.GetRequiredService<SignInService>().AnyUsersExistAsync())
        {
            _state.HasUsers = true;
        }

        bool isSetupPath = path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase);
        if (!_state.HasUsers && !isSetupPath)
        {
            context.Response.Redirect(SetupPath);
            return;
        }

        if (_state.HasUsers && isSetupPath)
        {
            context.Response.Redirect("/");
            return;
        }

        await _next(context);
    }

    private static bool IsStaticOrInternal(PathString path)
    {
        string? value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/icons", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/lib", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)
            || Path.HasExtension(value);
    }
}

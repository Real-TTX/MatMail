using System.Security.Claims;
using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>The resolved look of the current request. <see cref="UserHasMode"/> tells whether the user saved their own choice.</summary>
public sealed record ThemeChoice(string Mode, string Accent, bool UserHasMode, bool UserHasAccent = false);

/// <summary>Decides mode (system / light / dark) and accent colour: the signed-in user's choice, else the installation default.</summary>
public sealed class ThemeService
{
    public static readonly string[] Modes = { "system", "light", "dark" };
    public static readonly string[] Accents = { "blue", "green", "violet", "teal", "amber", "rose", "graphite" };

    private readonly IHttpContextAccessor _http;
    private readonly AppConfig _config;

    public ThemeService(IHttpContextAccessor http, AppConfig config)
    {
        _http = http;
        _config = config;
    }

    public ThemeChoice Resolve()
    {
        ClaimsPrincipal? principal = _http.HttpContext?.User;
        string? userMode = principal?.FindFirstValue(AppClaims.ThemeMode);
        string? userAccent = principal?.FindFirstValue(AppClaims.ThemeAccent);

        bool hasMode = Modes.Contains(userMode);
        string mode = hasMode ? userMode! : Normalize(_config.Display.ThemeMode, Modes, "system");
        bool hasAccent = Accents.Contains(userAccent);
        string accent = hasAccent ? userAccent! : Normalize(_config.Display.ThemeAccent, Accents, "blue");
        return new ThemeChoice(mode, accent, hasMode, hasAccent);
    }

    private static string Normalize(string? value, string[] allowed, string fallback)
        => allowed.Contains(value) ? value! : fallback;
}

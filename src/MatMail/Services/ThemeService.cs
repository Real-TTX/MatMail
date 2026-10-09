using System.Security.Claims;
using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>
/// The resolved look of the current request. <see cref="UserHasMode"/> tells whether the user saved their own choice.
/// Text size, density, previews and time zone are the user's own settings with a plain default.
/// </summary>
public sealed record ThemeChoice(string Mode, string Accent, bool UserHasMode, bool UserHasAccent = false)
{
    public string TextSize { get; init; } = "normal";
    public string Density { get; init; } = "comfortable";

    /// <summary>The first words of a message are shown in the list.</summary>
    public bool ShowPreviews { get; init; } = true;

    /// <summary>The time zone the user chose for dates and times; null: the server's (pages) and the browser's (mail client).</summary>
    public string? UserTimeZone { get; init; }

    /// <summary>Where the reader sits in the mail client: "off" (in place of the list), "right" or "below".</summary>
    public string ReadingPane { get; init; } = "off";

    /// <summary>The list of the mail client shows conversations.</summary>
    public bool ConversationView { get; init; }
}

/// <summary>Decides mode (system / light / dark), accent colour and the rest of the look: the signed-in user's choice, else the installation default.</summary>
public sealed class ThemeService
{
    public static readonly string[] Modes = { "system", "light", "dark" };
    public static readonly string[] Accents = { "blue", "green", "violet", "teal", "amber", "rose", "graphite" };
    public static readonly string[] TextSizes = { "small", "normal", "large" };
    public static readonly string[] Densities = { "comfortable", "compact" };
    public static readonly string[] ReadingPanes = { "off", "right", "below" };

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
        string? userZone = principal?.FindFirstValue(AppClaims.TimeZone);

        bool hasMode = Modes.Contains(userMode);
        string mode = hasMode ? userMode! : Normalize(_config.Display.ThemeMode, Modes, "system");
        bool hasAccent = Accents.Contains(userAccent);
        string accent = hasAccent ? userAccent! : Normalize(_config.Display.ThemeAccent, Accents, "blue");
        return new ThemeChoice(mode, accent, hasMode, hasAccent)
        {
            TextSize = Normalize(principal?.FindFirstValue(AppClaims.TextSize), TextSizes, "normal"),
            Density = Normalize(principal?.FindFirstValue(AppClaims.Density), Densities, "comfortable"),
            ShowPreviews = principal?.FindFirstValue(AppClaims.ShowPreviews) != "0",
            ReadingPane = Normalize(principal?.FindFirstValue(AppClaims.ReadingPane), ReadingPanes, "off"),
            ConversationView = principal?.FindFirstValue(AppClaims.ConversationView) == "1",
            UserTimeZone = Fmt.IsKnownZone(userZone) ? userZone : null,
        };
    }

    private static string Normalize(string? value, string[] allowed, string fallback)
        => allowed.Contains(value) ? value! : fallback;
}

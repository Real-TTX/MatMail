using System.Security.Claims;
using MatMail.Configuration;
using Microsoft.Extensions.Localization;

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
    /// <summary>"default" is the colour of the app (or of the tenant); the others are the presets of the toolkit (IDENT-05, data-accent on the html element).</summary>
    public static readonly string[] Accents = { "default", "purple", "indigo", "ocean", "teal", "forest", "gold", "sunset", "red", "berry", "graphite" };

    /// <summary>The accents with the names the pages show (the choice of the user, the default of the installation).</summary>
    public static IReadOnlyList<(string Key, string Label)> AccentChoices(IStringLocalizer<SharedResource> l) =>
    [
        ("default", l["Default"].Value), ("purple", l["Purple"].Value), ("indigo", l["Indigo"].Value), ("ocean", l["Ocean"].Value), ("teal", l["Teal"].Value),
        ("forest", l["Forest"].Value), ("gold", l["Gold"].Value), ("sunset", l["Sunset"].Value), ("red", l["Red"].Value), ("berry", l["Berry"].Value),
        ("graphite", l["Graphite"].Value),
    ];

    /// <summary>What earlier versions saved: the old names of the accents, which are presets of the toolkit now.</summary>
    private static readonly Dictionary<string, string> LegacyAccents = new(StringComparer.Ordinal) { ["blue"] = "default", ["green"] = "forest", ["violet"] = "purple", ["amber"] = "gold", ["rose"] = "berry" };
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
        // "default" is no choice: the colour of the tenant applies, then the default of the installation, then the colour of the app.
        string? savedAccent = NormalizeAccent(userAccent);
        if (savedAccent == "default")
        {
            savedAccent = null;
        }

        bool hasAccent = savedAccent is not null;
        string accent = savedAccent ?? NormalizeAccent(_config.Display.ThemeAccent) ?? "default";
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

    /// <summary>The accent as it is called now ("blue" of an earlier version is "default"); null for anything unknown.</summary>
    public static string? NormalizeAccent(string? value)
    {
        string? name = value is not null && LegacyAccents.TryGetValue(value, out string? current) ? current : value;
        return name is not null && Accents.Contains(name) ? name : null;
    }

    private static string Normalize(string? value, string[] allowed, string fallback)
        => allowed.Contains(value) ? value! : fallback;
}

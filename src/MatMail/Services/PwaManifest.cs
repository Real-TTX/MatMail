namespace MatMail.Services;

/// <summary>The manifest of the web app: what a phone or a desktop reads when MatMail is put on the home screen or installed like an app.</summary>
public static class PwaManifest
{
    /// <summary>The colour of the header in the light theme (<c>--color-bg</c>): the bar of the browser and the start screen of the app have it.</summary>
    public const string LightColour = "#f4f6fb";

    /// <summary>The same in the dark theme.</summary>
    public const string DarkColour = "#0f131c";

    /// <summary>
    /// The manifest as a document. <c>standalone</c> and not <c>fullscreen</c>: an iPhone cannot do fullscreen and then shows the page with
    /// dead strips at the edges; Android shows its status bar in the colour of the header. Colours follow the theme the person chose
    /// (the browser asks with its cookies, see the link in the layouts).
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Build(string name, string composeText, bool dark)
    {
        string colour = dark ? DarkColour : LightColour;
        string shortName = name.Length <= 12 ? name : name[..12].TrimEnd();
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["short_name"] = shortName,
            ["id"] = "/",
            ["start_url"] = "/Mail",
            ["scope"] = "/",
            ["display"] = "standalone",
            ["display_override"] = new[] { "standalone", "minimal-ui" },
            ["orientation"] = "any",
            ["background_color"] = colour,
            ["theme_color"] = colour,
            ["categories"] = new[] { "productivity", "business" },
            ["icons"] = new[]
            {
                Icon("/icons/icon-192.png", "192x192", "any"),
                Icon("/icons/icon-512.png", "512x512", "any"),
                Icon("/icons/icon-maskable-512.png", "512x512", "maskable"),
            },
            ["shortcuts"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = composeText,
                    ["short_name"] = composeText,
                    ["url"] = "/Mail?compose=1",
                    ["icons"] = new[] { Icon("/icons/icon-192.png", "192x192", "any") },
                },
            },
        };
    }

    private static Dictionary<string, object?> Icon(string source, string sizes, string purpose)
        => new() { ["src"] = source, ["sizes"] = sizes, ["type"] = "image/png", ["purpose"] = purpose };
}

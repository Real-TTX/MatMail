using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// What the pages need of the MatUi toolkit as text: the icon sprites, which every page inlines once (rule ICON-01), and the names in them.
/// The files themselves (<c>wwwroot/matui</c>) are a copy of the toolkit (rule CODE-03); <c>scripts/update-matui.ps1</c> brings a newer one.
/// The toolkit's own sprite has <c>mat-i-NAME</c>; the icons of MatMail that it does not have (reply, forward, a paperclip ...) come from the
/// icon library of MatDesign as <c>mat-il-NAME</c> (<c>scripts/build-icons.mjs</c>, rule ICON-05).
/// </summary>
public sealed partial class Toolkit
{
    public Toolkit(IWebHostEnvironment env) : this(env.WebRootFileProvider)
    {
    }

    public Toolkit(IFileProvider webRoot)
    {
        string toolkit = Read(webRoot, "matui/matui-icons.svg");
        string own = Read(webRoot, "matui/matmail-icons.svg");
        Sprite = toolkit + own;
        ToolkitIcons = Names(toolkit, "mat-i-");
        LibraryIcons = Names(own, "mat-il-");
    }

    /// <summary>Both sprites as markup: hidden <c>svg</c> elements with one <c>symbol</c> per icon.</summary>
    public string Sprite { get; }

    /// <summary>The icons of the toolkit's sprite (<c>mat-i-NAME</c>).</summary>
    public IReadOnlySet<string> ToolkitIcons { get; }

    /// <summary>The icons taken from the icon library (<c>mat-il-NAME</c>).</summary>
    public IReadOnlySet<string> LibraryIcons { get; }

    public bool HasIcon(string name) => ToolkitIcons.Contains(name) || LibraryIcons.Contains(name);

    /// <summary>The fragment a <c>use</c> element points to: the toolkit's symbol when it has one, else the library's.</summary>
    public string Symbol(string name) => LibraryIcons.Contains(name) && !ToolkitIcons.Contains(name) ? "#mat-il-" + name : "#mat-i-" + name;

    /// <summary>An icon as markup. The name has to be in one of the sprites (a unit test checks the pages and the scripts).</summary>
    public string IconMarkup(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        string css = string.IsNullOrEmpty(extraClass) ? "mat-icon" : "mat-icon " + WebUtility.HtmlEncode(extraClass);
        return $"<svg class=\"{css}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{Symbol(name)}\"></use></svg>";
    }

    public IHtmlContent Icon(string name, string? extraClass = null) => new HtmlString(IconMarkup(name, extraClass));

    /// <summary>
    /// The texts the script of the toolkit shows (the buttons of a confirmation, the messages of a form) as attributes of the html element, in the language of
    /// the page. The pickers know English and German themselves.
    /// </summary>
    public static IHtmlContent LanguageAttributes(IStringLocalizer<SharedResource> l)
    {
        (string Name, string Text)[] texts =
        [
            ("data-dlg-ok", l["OK"].Value), ("data-dlg-cancel", l["Cancel"].Value), ("data-dlg-close", l["Close"].Value), ("data-dlg-confirm", l["Please confirm"].Value),
            ("data-dlg-input", l["Input"].Value), ("data-dlg-notice", l["Notice"].Value), ("data-copied", l["Copied"].Value),
            ("data-msg-required", l["Please fill in this field."].Value), ("data-msg-type", l["Please enter a valid value."].Value), ("data-msg-pattern", l["The format is not valid."].Value),
        ];
        return new HtmlString(string.Join(' ', texts.Select(t => $"{t.Name}=\"{WebUtility.HtmlEncode(t.Text)}\"")));
    }

    private static string Read(IFileProvider webRoot, string path)
    {
        IFileInfo file = webRoot.GetFileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The toolkit file wwwroot/{path} is missing (see scripts/update-matui.ps1 and scripts/build-icons.mjs).");
        }

        using var reader = new StreamReader(file.CreateReadStream());
        return reader.ReadToEnd();
    }

    private static HashSet<string> Names(string sprite, string prefix)
        => new(SymbolRegex().Matches(sprite).Select(m => m.Groups[1].Value).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => id[prefix.Length..]), StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"<symbol id=""([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();
}

/// <summary>Small helpers for the markup of the toolkit.</summary>
public static class Ui
{
    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? "?"
            : string.Concat(name.Split([' ', '-', '_', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Text with the characters of HTML escaped, for the few places that build markup in C#.</summary>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>How the toolkit's registry knows MatMail (the accent itself comes from <c>data-app</c> in <c>matui.css</c>).</summary>
    public static class App
    {
        public const string Id = "matmail";
        public const string Name = "MatMail";
        public const string Tagline = "Mail gateway and web mail client";

        /// <summary>The accent as the toolkit registry has it; it colours the bar of the browser. A unit test compares it with <c>matui.css</c>.</summary>
        public const string Accent = "#1a56db";
    }
}

using System.Text.RegularExpressions;
using MatMail.Controls;
using MatMail.Services;
using Microsoft.Extensions.FileProviders;

namespace MatMail.Tests;

/// <summary>
/// The pages are built from the MatUi toolkit (rulebook of MatDesign): what is checked here is what a person could break by hand without seeing it —
/// an icon that does not exist, an inline style, a window.confirm, a literal colour, a menu that points nowhere.
/// </summary>
public partial class ToolkitTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Source = Path.Combine(Root, "src", "MatMail");

    private static string FindRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MatMail.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return root;
    }

    private static IEnumerable<string> Files(string folder, string pattern)
        => Directory.EnumerateFiles(Path.Combine(Source, folder), pattern, SearchOption.AllDirectories);

    private static Toolkit LoadToolkit() => new(new PhysicalFileProvider(Path.Combine(Source, "wwwroot")));

    // ---- icons ----

    [Fact]
    public void The_two_sprites_are_read_and_every_icon_has_one_address()
    {
        Toolkit toolkit = LoadToolkit();

        Assert.Contains("save", toolkit.ToolkitIcons);
        Assert.Contains("reply", toolkit.LibraryIcons);
        Assert.Equal("#mat-i-save", toolkit.Symbol("save"));
        Assert.Equal("#mat-il-reply", toolkit.Symbol("reply"));
        Assert.Contains("<use href=\"#mat-il-reply\">", toolkit.IconMarkup("reply"));
        Assert.Throws<ArgumentException>(() => toolkit.IconMarkup("\"><script>"));
    }

    [Fact]
    public void Every_icon_that_a_page_a_control_or_a_script_asks_for_is_in_a_sprite()
    {
        Toolkit toolkit = LoadToolkit();
        var wanted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string file)
        {
            if (name.Length > 0 && !wanted.ContainsKey(name))
            {
                wanted[name] = Path.GetRelativePath(Source, file);
            }
        }

        void Collect(string file, Regex pattern)
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                foreach (Group group in match.Groups.Values.Skip(1))
                {
                    Add(group.Value, file);
                }
            }
        }

        foreach (string file in Files("Pages", "*.cshtml"))
        {
            Collect(file, MarkupIcon());
        }

        foreach (string file in Files("Controls", "*.cs").Concat(Files("Services", "*.cs")))
        {
            Collect(file, CodeIcon());
        }

        foreach (string file in Files(Path.Combine("wwwroot", "js"), "*.js"))
        {
            Collect(file, ScriptIcon());
            Match kinds = Regex.Match(File.ReadAllText(file), @"KIND_ICONS\s*=\s*\{([^}]*)\}");
            if (kinds.Success)
            {
                foreach (Match kind in Regex.Matches(kinds.Groups[1].Value, @":\s*""([a-z0-9-]+)"""))
                {
                    Add(kind.Groups[1].Value, file);
                }
            }
        }

        foreach (NavSet menu in ShellMenusOfASystemAdministrator())
        {
            Add(menu.Icon, "Services/ShellMenus.cs");
            foreach (NavItem item in menu.Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children!.Append(i) : [i]))
            {
                Add(item.Icon ?? string.Empty, "Services/ShellMenus.cs");
            }
        }

        Assert.NotEmpty(wanted);
        var missing = wanted.Where(w => !toolkit.HasIcon(w.Key)).Select(w => $"{w.Key} ({w.Value})").ToList();
        Assert.True(missing.Count == 0, "Icons that are in no sprite: " + string.Join(", ", missing));
    }

    // ---- the app in the registry ----

    [Fact]
    public void The_accent_of_the_app_is_the_one_in_the_style_sheet_of_the_toolkit()
    {
        string css = File.ReadAllText(Path.Combine(Source, "wwwroot", "matui", "matui.css"));
        Match match = Regex.Match(css, @"\[data-app=""matmail""\]\s*\{\s*--mat-app-accent:\s*(#[0-9a-fA-F]{6})", RegexOptions.IgnoreCase);

        Assert.True(match.Success, "matui.css has no accent for data-app=\"matmail\".");
        Assert.Equal(Ui.App.Accent, match.Groups[1].Value, ignoreCase: true);
    }

    [Theory]
    [InlineData("blue", "default")]
    [InlineData("green", "forest")]
    [InlineData("violet", "purple")]
    [InlineData("amber", "gold")]
    [InlineData("rose", "berry")]
    [InlineData("teal", "teal")]
    [InlineData("graphite", "graphite")]
    [InlineData("ocean", "ocean")]
    public void The_old_accents_of_an_earlier_version_are_presets_of_the_toolkit_now(string saved, string expected)
        => Assert.Equal(expected, ThemeService.NormalizeAccent(saved));

    [Theory]
    [InlineData("orange")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("custom")]
    public void An_accent_nobody_offers_is_not_taken(string? saved) => Assert.Null(ThemeService.NormalizeAccent(saved));

    // ---- the pages: what the rulebook forbids is not there ----

    [Fact]
    public void No_page_has_an_inline_style_an_inline_script_or_an_inline_handler()
    {
        var found = new List<string>();
        foreach (string file in Files("Pages", "*.cshtml"))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(Source, file);
            if (InlineStyle().IsMatch(text))
            {
                found.Add(relative + ": style=");
            }

            if (InlineScript().IsMatch(text))
            {
                found.Add(relative + ": <script> without src");
            }

            if (InlineHandler().IsMatch(text))
            {
                found.Add(relative + ": onclick= and the like");
            }
        }

        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void No_script_of_the_app_opens_a_dialog_of_the_browser()
    {
        var found = new List<string>();
        foreach (string file in Files(Path.Combine("wwwroot", "js"), "*.js"))
        {
            string text = StripComments(File.ReadAllText(file));
            if (BrowserDialog().IsMatch(text))
            {
                found.Add(Path.GetFileName(file));
            }
        }

        Assert.True(found.Count == 0, "window.confirm/alert/prompt in: " + string.Join(", ", found));
    }

    [Fact]
    public void The_style_sheets_of_the_app_have_no_colour_of_their_own()
    {
        var found = new List<string>();
        foreach (string name in new[] { "matmail.css", "mail.css" })
        {
            string css = Regex.Replace(File.ReadAllText(Path.Combine(Source, "wwwroot", "css", name)), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match literal in LiteralColour().Matches(css))
            {
                found.Add($"{name}: {literal.Value}");
            }
        }

        Assert.True(found.Count == 0, "Literal colours (use the --mat-* tokens): " + string.Join(", ", found));
    }

    [Fact]
    public void The_old_design_is_gone()
    {
        Assert.False(File.Exists(Path.Combine(Source, "wwwroot", "css", "app.css")));
        Assert.False(File.Exists(Path.Combine(Source, "wwwroot", "css", "components.css")));
        Assert.False(File.Exists(Path.Combine(Source, "wwwroot", "js", "app.js")));
        Assert.False(File.Exists(Path.Combine(Source, "wwwroot", "js", "picker.js")));
        Assert.True(File.Exists(Path.Combine(Source, "wwwroot", "matui", "matui.css")));
        Assert.True(File.Exists(Path.Combine(Source, "wwwroot", "matui", "matui.js")));
        Assert.True(File.Exists(Path.Combine(Source, "wwwroot", "matui", "matui.boot.js")));
    }

    // ---- the menus ----

    [Theory]
    [InlineData("/Admin/Mailboxes", "/Admin/Mailboxes")]
    [InlineData("/Admin/Mailboxes/Edit", "/Admin/Mailboxes")]
    [InlineData("/admin/mailboxes/addresses", "/Admin/Mailboxes")]
    [InlineData("/Admin", "/Admin")]
    [InlineData("/Admin/Backup/Plans", "/Admin/Backup")]
    [InlineData("/Account/Rules/Edit", "/Account/Rules")]
    [InlineData("/Account", "/Account")]
    [InlineData("/Nowhere", null)]
    public void A_page_belongs_to_the_item_of_the_menu_whose_address_it_continues(string path, string? expected)
        => Assert.Equal(expected, ShellMenus.ActiveHref(Menus(), path));

    [Theory]
    [InlineData("/Mail", "mail")]
    [InlineData("/Admin/Users/Edit", "admin")]
    [InlineData("/Admin/Backup/Plan", "admin")]
    [InlineData("/Account/Security", "account")]
    [InlineData("/Account/Rules/Edit", "account")]
    [InlineData("/", "mail")]
    public void A_page_shows_the_menu_of_its_area(string path, string expectedId)
        => Assert.Equal(expectedId, ShellMenus.Current(Menus(), path).Id);

    [Fact]
    public void A_closed_parent_shows_the_sum_of_the_badges_of_its_children()
    {
        Assert.Equal("99+", NavBadge.Count(120).Text);
        NavBadge? sum = NavBadge.Sum([NavBadge.Count(2, "warn"), NavBadge.Count(3, "error")]);

        Assert.Equal("5", sum!.Text);
        Assert.Equal("error", sum.Tone);
        Assert.True(NavBadge.Sum([new NavBadge(string.Empty, "warn"), NavBadge.Count(1)])!.IsDot);
        Assert.Null(NavBadge.Sum([]));
        Assert.Equal("mat-nav__badge mat-nav__badge--warn mat-nav__badge--dot", new NavBadge(string.Empty, "warn").Classes());
    }

    /// <summary>A system administrator holds every permission, so this is every menu the shell can show.</summary>
    private static IReadOnlyList<NavSet> ShellMenusOfASystemAdministrator()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(AppClaims.SystemAdmin, "1")], "test"));
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal } };
        return new ShellMenus(new CurrentUser(accessor), new EchoLocalizer()).ForUser();
    }

    private sealed class EchoLocalizer : Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>
    {
        public Microsoft.Extensions.Localization.LocalizedString this[string name] => new(name, name);

        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));

        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    [Fact]
    public void Every_item_of_every_menu_leads_to_a_page_and_has_an_icon_that_exists()
    {
        Toolkit toolkit = LoadToolkit();
        IReadOnlyList<NavSet> menus = ShellMenusOfASystemAdministrator();

        Assert.Equal(["mail", "admin", "account"], menus.Select(m => m.Id));
        foreach (NavSet menu in menus)
        {
            Assert.True(toolkit.HasIcon(menu.Icon), $"menu {menu.Id}: icon {menu.Icon}");
            foreach (NavItem page in menu.Pages)
            {
                string folder = page.Href!.Trim('/').Replace('/', Path.DirectorySeparatorChar);
                bool exists = File.Exists(Path.Combine(Source, "Pages", folder + ".cshtml")) || File.Exists(Path.Combine(Source, "Pages", folder, "Index.cshtml"));
                Assert.True(exists, $"{page.Href} ({page.Title}) has no page");
                Assert.True(page.Icon is null || toolkit.HasIcon(page.Icon), $"{page.Href}: icon {page.Icon}");
            }
        }
    }

    [Fact]
    public void Every_menu_has_a_start_page_that_one_of_its_own_pages_is()
    {
        foreach (NavSet menu in ShellMenusOfASystemAdministrator())
        {
            Assert.True(menu.HasStartPage, $"menu {menu.Id} has no start page");   // the switcher loads it, so the entry is a link
            Assert.Contains(menu.Pages, page => page.Href == menu.Href);
            Assert.True(menu.Contains(menu.Href), $"{menu.Href} does not belong to the menu {menu.Id}");
        }
    }

    private static IReadOnlyList<NavSet> Menus() =>
    [
        new("mail", "Mail", "mail", [new(null, [new("Open mail", "/Mail", "inbox")])], Custom: true, Paths: ["/Mail"]),
        new("admin", "Administration", "settings", [new(null, [new("Dashboard", "/Admin", "dashboard")]), new("Mail", [new("Mailboxes", "/Admin/Mailboxes", "inbox"), new("Backups", "/Admin/Backup", "archive")])]),
        new("account", "My account", "user", [new(null, [new("Profile", "/Account", "user"), new("Mail rules", "/Account/Rules", "filter")])], Paths: ["/Account"]),
    ];

    // ---- the table ----

    [Fact]
    public void Every_cell_of_a_table_gets_the_name_of_its_column()
    {
        const string html = "<thead><tr><th>Name</th><th class=\"mat-hide-sm\">Type</th><th scope=\"col\"><span class=\"mat-sr-only\">Actions</span></th></tr></thead>" +
                            "<tbody><tr data-row-href=\"/x\"><td>A</td><td class=\"mat-hide-sm\">B</td><td class=\"mat-table__cell-actions\">C</td></tr>" +
                            "<tr><td data-label=\"Own\">D</td><td>E</td><td>F</td></tr></tbody>";

        string result = TableTagHelper.AddCellLabels(html);

        Assert.Contains("<td data-label=\"Name\">A</td>", result);
        Assert.Contains("<td class=\"mat-hide-sm\" data-label=\"Type\">B</td>", result);
        Assert.Contains("<td class=\"mat-table__cell-actions\">C</td>", result);   // a heading that only a screen reader sees names nothing
        Assert.Contains("<td data-label=\"Own\">D</td>", result);
        Assert.Contains("<td data-label=\"Type\">E</td>", result);
    }

    [Fact]
    public void A_cell_under_a_heading_that_spans_columns_is_counted_by_columns()
    {
        const string html = "<thead><tr><th colspan=\"2\">Who</th><th>When</th></tr></thead><tbody><tr><td>A</td><td>B</td><td>C</td></tr></tbody>";

        string result = TableTagHelper.AddCellLabels(html);

        Assert.Contains("<td data-label=\"Who\">A</td>", result);
        Assert.Contains("<td>B</td>", result);
        Assert.Contains("<td data-label=\"When\">C</td>", result);
    }

    // ---- patterns ----

    [GeneratedRegex(@"(?:<mm-icon\b[^>]*?\bname|(?<![-\w])icon|\bempty-icon)=""([a-z0-9-]+)""")]
    private static partial Regex MarkupIcon();

    [GeneratedRegex(@"(?:IconMarkup|Icon|Symbol)\(\s*""([a-z0-9-]+)""|EmptyIcon\s*=\s*""([a-z0-9-]+)""|Icon\s*\?\?\s*""([a-z0-9-]+)""")]
    private static partial Regex CodeIcon();

    [GeneratedRegex(@"(?:\.icon|iconButton|\bicon|[\s(]button|replyButton|toolButton)\(\s*""([a-z0-9-]+)""|\bicon:\s*""([a-z0-9-]+)""")]
    private static partial Regex ScriptIcon();

    [GeneratedRegex(@"\sstyle\s*=\s*""")]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)(?![^>]*type=""application/json"")[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\son(click|change|submit|input|load|error|focus|blur|keydown|keyup|mouse\w+)\s*=\s*""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();

    [GeneratedRegex(@"(?<![\w.$])(?:window\.)?(?:confirm|alert|prompt)\s*\(")]
    private static partial Regex BrowserDialog();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(")]
    private static partial Regex LiteralColour();

    private static string StripComments(string script)
        => Regex.Replace(Regex.Replace(script, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"(?<![:""'])//[^\n]*", string.Empty);
}

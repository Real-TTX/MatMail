using System.Globalization;
using System.Text.RegularExpressions;

namespace MatMail.Controls;

/// <summary>
/// A badge at the right end of a menu item (SHELL-14): a count, or a dot for "something new" (empty text). <see cref="Tone"/> is one of
/// accent, ok, warn, error, info, plain and follows the meaning of the status colours (COLOR-03). <see cref="Label"/> is what a screen reader says ("3 failed").
/// </summary>
public sealed record NavBadge(string Text, string Tone = "accent", string? Label = null)
{
    private static readonly string[] Severity = ["error", "warn", "info", "accent", "ok", "plain"];

    public bool IsDot => Text.Length == 0;

    /// <summary>A count; above 99 it reads "99+".</summary>
    public static NavBadge Count(int number, string tone = "accent", string? label = null)
        => new(number > 99 ? "99+" : number.ToString(CultureInfo.InvariantCulture), tone, label);

    /// <summary>The classes of the badge; <paramref name="sum"/> marks the stand-in of a closed parent for its children's badges.</summary>
    public string Classes(bool sum = false)
        => "mat-nav__badge" + (Tone == "accent" ? string.Empty : $" mat-nav__badge--{Tone}") + (IsDot ? " mat-nav__badge--dot" : string.Empty) + (sum ? " mat-nav__badge--sum" : string.Empty);

    /// <summary>The badge of a closed parent: the sum of its children's counts, or a dot of the most urgent tone when they are not all counts.</summary>
    public static NavBadge? Sum(IEnumerable<NavBadge> badges)
    {
        var list = badges.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        string tone = Severity.First(t => list.Any(b => b.Tone == t));
        var counts = list.Where(b => !b.IsDot && int.TryParse(b.Text.TrimEnd('+'), NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        string label = string.Join(", ", list.Select(b => b.Label).Where(l => l is not null));
        return counts.Count == list.Count
            ? Count(counts.Sum(b => int.Parse(b.Text.TrimEnd('+'), CultureInfo.InvariantCulture)), tone, label)
            : new NavBadge(string.Empty, tone, label);
    }
}

/// <summary>
/// An item of the menu. With <see cref="Children"/> it is the parent of a submenu (SHELL-13): not a page, a button that opens its children, one level deep.
/// A parent has no <see cref="Href"/>; if the section has a page of its own, that page is its first child. Children show no icon.
/// </summary>
public sealed record NavItem(string Title, string? Href, string? Icon, NavBadge? Badge = null, IReadOnlyList<NavItem>? Children = null)
{
    public bool IsParent => Children is { Count: > 0 };

    /// <summary>A stable key for remembering whether the user opened the branch.</summary>
    public string Key => Regex.Replace(Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public bool Contains(string? href) => href is not null && (Href == href || (Children?.Any(c => c.Href == href) ?? false));

    /// <summary>What a closed parent shows when it has no badge of its own: the sum of its children's badges.</summary>
    public NavBadge? SumOfChildren => IsParent && Badge is null ? NavBadge.Sum(Children!.Where(c => c.Badge is not null).Select(c => c.Badge!)) : null;
}

public sealed record NavGroup(string? Title, IReadOnlyList<NavItem> Items);

/// <summary>
/// One menu of the app (SHELL-15). The switcher under the logo shows one at a time; with a single menu there is no switcher.
/// <see cref="Custom"/> is a menu whose panel the page draws itself (the folders of the mail client): it names the pages it belongs to by <see cref="Paths"/>.
/// With a <see cref="Href"/> choosing the menu in the switcher loads that page (its start page) instead of swapping the items: every menu of MatMail is an
/// area with pages of its own, so the page that loads draws its own menu and the other pages carry no hidden copy of it.
/// </summary>
public sealed record NavSet(string Id, string Title, string Icon, IReadOnlyList<NavGroup> Groups, bool Custom = false, IReadOnlyList<string>? Paths = null, string? Href = null)
{
    public bool HasStartPage => Href is not null;

    /// <summary>The pages of the menu (parents are not pages).</summary>
    public IEnumerable<NavItem> Pages => Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children! : [i]).Where(i => i.Href is not null);

    public bool Contains(string? path)
        => path is not null && (Pages.Any(i => i.Href == path) || (Paths?.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)) ?? false));

    /// <summary>True when an item of this menu asks for attention (a warning or error badge).</summary>
    public bool NeedsAttention => Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children!.Prepend(i) : [i]).Any(i => i.Badge is { Tone: "warn" or "error" });
}

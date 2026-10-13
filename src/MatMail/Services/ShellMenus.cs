using MatMail.Controls;
using Microsoft.Extensions.Localization;

namespace MatMail.Services;

/// <summary>
/// The menus of the shell (SHELL-15): Mail (the web client draws its folders itself), Administration and the user's Account. What a person sees
/// depends on what they may do; with more than one menu the switcher under the logo appears. The pages know which menu they belong to by their address.
/// </summary>
public sealed class ShellMenus(CurrentUser user, IStringLocalizer<SharedResource> l)
{
    public const string MailId = "mail";
    public const string AdminId = "admin";
    public const string AccountId = "account";

    public IReadOnlyList<NavSet> ForUser()
    {
        var menus = new List<NavSet>();
        if (user.Can(Permissions.MailUse))
        {
            menus.Add(Mail());
        }

        menus.Add(Account());
        if (user.CanAdminister)
        {
            menus.Add(Admin());   // last: below the tenants in the popup of the switcher
        }

        return menus;
    }

    /// <summary>The mail client: on its own page the sidebar shows the folders; choosing the menu opens that page, so the link is only for a page of the area that has no sidebar.</summary>
    private NavSet Mail()
        => new(MailId, l["Mail"].Value, "mail", [new(null, [new(l["Open mail"].Value, "/Mail", "inbox")])], Custom: true, Paths: ["/Mail"], Href: "/Mail");

    private NavSet Admin()
    {
        var groups = new List<NavGroup>();
        void Group(string? title, params (string Href, string Icon, string Text, string? Permission, bool SystemOnly)[] entries)
        {
            var items = entries.Where(e => e.SystemOnly ? user.IsSystemAdmin : e.Permission is null || user.Can(e.Permission))
                .Select(e => new NavItem(e.Text, e.Href, e.Icon)).ToList();
            if (items.Count > 0)
            {
                groups.Add(new NavGroup(title, items));
            }
        }

        Group(null, ("/Admin", "dashboard", l["Dashboard"].Value, null, false));
        Group(l["Mail"].Value,
            ("/Admin/Mailboxes", "inbox", l["Mailboxes"].Value, Permissions.MailboxesManage, false),
            ("/Admin/Domains", "globe", l["Domains"].Value, Permissions.DomainsManage, false),
            ("/Admin/Accounts", "plug", l["Connected accounts"].Value, Permissions.AccountsManage, false),
            ("/Admin/Signatures", "pen-line", l["Signatures"].Value, Permissions.SignaturesManage, false),
            ("/Admin/Templates", "file-text", l["Templates"].Value, Permissions.SignaturesManage, false),
            ("/Admin/Unassigned", "alert", l["Unassigned"].Value, Permissions.UnassignedManage, false));
        Group(l["People"].Value,
            ("/Admin/Users", "users", l["Users"].Value, Permissions.UsersManage, false),
            ("/Admin/Roles", "shield", l["Roles"].Value, Permissions.RolesManage, false),
            ("/Admin/Directories", "server", l["Directories"].Value, Permissions.DirectoriesManage, false));
        Group(l["Delivery"].Value,
            ("/Admin/Relay", "network", l["SMTP relay"].Value, Permissions.RelayManage, false),
            ("/Admin/Queue", "send", l["Outgoing queue"].Value, Permissions.QueueManage, false),
            ("/Admin/Transfers", "shuffle", l["Mail transfers"].Value, Permissions.LogsView, false));
        Group(l["System"].Value,
            ("/Admin/Logs", "activity", l["Activity log"].Value, Permissions.LogsView, false),
            ("/Admin/Branding", "image", l["Branding"].Value, Permissions.BrandingManage, false),
            ("/Admin/Security", "lock", l["Security"].Value, Permissions.SecurityManage, false),
            ("/Admin/Backup", "archive", l["Backups"].Value, null, true),
            ("/Admin/Settings", "settings", l["Server settings"].Value, null, true),
            ("/Admin/Tenants", "building", l["Tenants"].Value, null, true));
        return new NavSet(AdminId, l["Administration"].Value, "settings", groups, Href: "/Admin", Last: true);
    }

    private NavSet Account()
    {
        var items = new List<NavItem>
        {
            new(l["Profile"].Value, "/Account", "user"),
            new(l["Password"].Value, "/Account/Password", "key"),
            new(l["Security"].Value, "/Account/Security", "shield"),
            new(l["Sessions"].Value, "/Account/Sessions", "monitor"),
            new(l["Mail programs"].Value, "/Account/MailPrograms", "mail"),
        };
        if (user.Can(Permissions.MailUse))
        {
            items.Add(new NavItem(l["Mail rules"].Value, "/Account/Rules", "filter"));
        }

        items.Add(new NavItem(l["Appearance"].Value, "/Account/Appearance", "sliders"));
        items.Add(new NavItem(l["App"].Value, "/Account/App", "smartphone"));
        return new NavSet(AccountId, l["My account"].Value, "user", [new(null, items)], Paths: ["/Account"], Href: "/Account");
    }

    /// <summary>
    /// The page of the menus that belongs to <paramref name="path"/>: an address that is the same, or the longest one the path continues
    /// (<c>/Admin/Mailboxes/Edit</c> belongs to <c>/Admin/Mailboxes</c>). The roots (<c>/Admin</c>, <c>/Account</c>) are only themselves.
    /// </summary>
    public static string? ActiveHref(IEnumerable<NavSet> sets, string path)
        => sets.SelectMany(s => s.Pages).Select(i => i.Href!)
            .Where(h => path.Equals(h, StringComparison.OrdinalIgnoreCase) || (h is not ("/Admin" or "/Account") && path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(h => h.Length)
            .FirstOrDefault();

    /// <summary>The menu the page belongs to: the one that has its address, else the one of its area, else the first.</summary>
    public static NavSet Current(IReadOnlyList<NavSet> menus, string path)
    {
        string? active = ActiveHref(menus, path);
        NavSet? hit = active is null ? null : menus.FirstOrDefault(m => m.Contains(active));
        hit ??= menus.FirstOrDefault(m => m.Contains(path));
        hit ??= menus.FirstOrDefault(m => path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) && m.Id == AdminId);
        hit ??= menus.FirstOrDefault(m => path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase) && m.Id == AccountId);
        return hit ?? menus[0];
    }
}

namespace MatMail.Services;

/// <summary>
/// The permission catalogue. Roles are sets of these strings; a user's permissions are the union of the roles they hold.
/// Installation-wide operations (tenants, server settings) are not permissions: they belong to system administrators.
/// Permissions control the admin area and the right to use mail at all; access to a particular mailbox is delegated
/// separately (see <c>MailboxPermission</c>), so an administrator does not automatically read other people's mail.
/// </summary>
public static class Permissions
{
    public const string MailUse = "mail.use";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string DomainsManage = "domains.manage";
    public const string MailboxesManage = "mailboxes.manage";
    public const string AccountsManage = "accounts.manage";
    public const string SignaturesManage = "signatures.manage";
    public const string RelayManage = "relay.manage";
    public const string QueueManage = "queue.manage";
    public const string LogsView = "logs.view";
    public const string UnassignedManage = "unassigned.manage";
    public const string BrandingManage = "branding.manage";

    /// <summary>Policy name for pages only system administrators may open.</summary>
    public const string SystemAdminPolicy = "SystemAdmin";

    /// <summary>Permission → (group, description) in display order; used by the role editor.</summary>
    public static readonly IReadOnlyList<PermissionInfo> Catalogue = new[]
    {
        new PermissionInfo(MailUse, "Mail", "Use the web mail client, IMAP and SMTP"),
        new PermissionInfo(UnassignedManage, "Mail", "Work with the \"Unassigned\" mailbox and distribute its messages"),
        new PermissionInfo(UsersManage, "Administration", "Manage users"),
        new PermissionInfo(RolesManage, "Administration", "Manage roles and permissions"),
        new PermissionInfo(DomainsManage, "Administration", "Manage domains"),
        new PermissionInfo(MailboxesManage, "Administration", "Manage mailboxes, addresses and delegation"),
        new PermissionInfo(AccountsManage, "Administration", "Manage connected provider accounts"),
        new PermissionInfo(SignaturesManage, "Administration", "Manage signatures, footers and mail templates"),
        new PermissionInfo(RelayManage, "Administration", "Manage SMTP relay rules"),
        new PermissionInfo(QueueManage, "Administration", "Manage the outgoing queue"),
        new PermissionInfo(LogsView, "Administration", "View the activity log"),
        new PermissionInfo(BrandingManage, "Administration", "Manage the branding of the tenant: name, logo and colour"),
    };

    public static readonly IReadOnlyList<string> All = Catalogue.Select(p => p.Key).ToArray();

    /// <summary>What a plain user gets.</summary>
    public static readonly IReadOnlyList<string> UserDefaults = new[] { MailUse };

    public static bool IsKnown(string permission) => All.Contains(permission, StringComparer.Ordinal);
}

public sealed record PermissionInfo(string Key, string Group, string Description);

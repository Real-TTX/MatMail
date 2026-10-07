using MailKit;
using MatMail.Data;
using MatMail.Messaging;

namespace MatMail.MailSync;

/// <summary>A remote folder as the synchronisation sees it.</summary>
public sealed record RemoteFolderInfo(string FullName, string Name, char Separator, FolderAttributes Attributes)
{
    /// <summary>The POP3 maildrop, which behaves like a single inbox.</summary>
    public static RemoteFolderInfo Pop3Inbox { get; } = new("INBOX", "INBOX", '/', FolderAttributes.Inbox);

    public static RemoteFolderInfo From(IMailFolder folder) => new(folder.FullName, folder.Name, folder.DirectorySeparator, folder.Attributes);
}

/// <summary>How remote folders map onto local ones: special folders by their RFC 6154 attributes or common names, the rest by path.</summary>
public static class RemoteFolderMap
{
    private static readonly Dictionary<string, FolderKind> NameKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = FolderKind.Sent,
        ["Sent Items"] = FolderKind.Sent,
        ["Sent Messages"] = FolderKind.Sent,
        ["Sent Mail"] = FolderKind.Sent,
        ["Gesendet"] = FolderKind.Sent,
        ["Gesendete Elemente"] = FolderKind.Sent,
        ["Gesendete Objekte"] = FolderKind.Sent,
        ["Gesendete Nachrichten"] = FolderKind.Sent,
        ["Drafts"] = FolderKind.Drafts,
        ["Draft"] = FolderKind.Drafts,
        ["Entwürfe"] = FolderKind.Drafts,
        ["Entwurf"] = FolderKind.Drafts,
        ["Trash"] = FolderKind.Trash,
        ["Deleted Items"] = FolderKind.Trash,
        ["Deleted Messages"] = FolderKind.Trash,
        ["Bin"] = FolderKind.Trash,
        ["Papierkorb"] = FolderKind.Trash,
        ["Gelöschte Elemente"] = FolderKind.Trash,
        ["Gelöschte Objekte"] = FolderKind.Trash,
        ["Junk"] = FolderKind.Junk,
        ["Junk E-mail"] = FolderKind.Junk,
        ["Junk-E-Mail"] = FolderKind.Junk,
        ["Spam"] = FolderKind.Junk,
        ["Spamverdacht"] = FolderKind.Junk,
        ["Bulk Mail"] = FolderKind.Junk,
        ["Archive"] = FolderKind.Archive,
        ["Archives"] = FolderKind.Archive,
        ["Archiv"] = FolderKind.Archive,
    };

    /// <summary>Which local special folder a remote folder stands for (Inbox, Sent, ...), or <see cref="FolderKind.Custom"/>.</summary>
    public static FolderKind KindOf(RemoteFolderInfo folder)
    {
        if (folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) || folder.Attributes.HasFlag(FolderAttributes.Inbox))
        {
            return FolderKind.Inbox;
        }

        FolderKind byAttribute = KindOfAttributes(folder.Attributes);
        if (byAttribute != FolderKind.Custom)
        {
            return byAttribute;
        }

        // Servers without RFC 6154 attributes: the common names decide, at the top level or directly below the inbox ("INBOX.Sent").
        return IsTopLevel(folder) && NameKinds.TryGetValue(folder.Name.Trim(), out FolderKind kind) ? kind : FolderKind.Custom;
    }

    /// <summary>Folders that only show messages of other folders (e.g. Gmail "Starred", "Important"); copying them would only duplicate.</summary>
    public static bool IsVirtual(RemoteFolderInfo folder)
        => folder.Attributes.HasFlag(FolderAttributes.Flagged) || folder.Attributes.HasFlag(FolderAttributes.Important);

    /// <summary>
    /// Everyday mail with "all folders": the inbox and the custom folders (e.g. filled by server-side rules). Sent, drafts, trash,
    /// junk, archive and "all mail" folders hold no incoming mail and are left out.
    /// </summary>
    public static bool IsIncomingFolder(RemoteFolderInfo folder)
        => !IsVirtual(folder) && !folder.Attributes.HasFlag(FolderAttributes.All) && (KindOf(folder) is FolderKind.Inbox or FolderKind.Custom);

    /// <summary>Backup and migration copy every selectable folder except the purely virtual ones.</summary>
    public static bool IsCopiedFolder(RemoteFolderInfo folder) => !IsVirtual(folder);

    /// <summary>The local path mirroring a remote folder: "INBOX.Projects.2026" (separator '.') becomes "INBOX/Projects/2026".</summary>
    public static string LocalPathOf(RemoteFolderInfo folder)
    {
        string[] parts = SplitPath(folder).Select(SanitizeSegment).ToArray();
        if (parts[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            parts[0] = "INBOX";
        }

        return string.Join(FolderService.Separator, parts);
    }

    /// <summary>Where a backup keeps a remote folder: "Backup/&lt;account name&gt;/&lt;remote path&gt;".</summary>
    public static string BackupPathOf(string accountName, RemoteFolderInfo folder)
        => string.Join(FolderService.Separator, "Backup", SanitizeSegment(accountName), LocalPathOf(folder));

    /// <summary>A folder name segment the local store accepts: no "/" or "\", not "." or "..", at most 200 characters.</summary>
    public static string SanitizeSegment(string name)
    {
        string clean = name.Replace(FolderService.Separator, '-').Replace('\\', '-').Trim();
        if (clean.Length == 0 || clean is "." or "..")
        {
            clean = "_";
        }

        return clean.Length > 200 ? clean[..200].Trim() : clean;
    }

    private static FolderKind KindOfAttributes(FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Sent))
        {
            return FolderKind.Sent;
        }

        if (attributes.HasFlag(FolderAttributes.Drafts))
        {
            return FolderKind.Drafts;
        }

        if (attributes.HasFlag(FolderAttributes.Trash))
        {
            return FolderKind.Trash;
        }

        if (attributes.HasFlag(FolderAttributes.Junk))
        {
            return FolderKind.Junk;
        }

        return attributes.HasFlag(FolderAttributes.Archive) ? FolderKind.Archive : FolderKind.Custom;
    }

    private static bool IsTopLevel(RemoteFolderInfo folder)
    {
        string[] parts = SplitPath(folder);
        return parts.Length == 1 || (parts.Length == 2 && parts[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitPath(RemoteFolderInfo folder)
    {
        string[] parts = folder.Separator == '\0'
            ? new[] { folder.FullName }
            : folder.FullName.Split(folder.Separator, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? new[] { folder.FullName } : parts;
    }
}

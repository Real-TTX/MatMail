using System.Text;
using System.Text.RegularExpressions;
using MatMail.Data;
using MatMail.Messaging;

namespace MatMail.MailServer.Imap;

/// <summary>
/// One name in the IMAP hierarchy: a folder, or a node that only groups others ("Shared", "Shared/Info") and cannot be selected.
/// </summary>
internal sealed class ImapMailboxNode
{
    /// <summary>The full IMAP name (Unicode), e.g. "INBOX", "Archive/2025" or "Shared/Info/INBOX".</summary>
    public required string Name { get; init; }

    /// <summary>The folder; null for grouping nodes.</summary>
    public FolderInfo? Folder { get; init; }

    /// <summary>The mailbox the node belongs to; null for the "Shared" root.</summary>
    public AccessibleMailbox? Mailbox { get; init; }

    /// <summary>A folder of the user's own mailbox (the personal namespace).</summary>
    public bool IsPersonal { get; init; }

    public bool HasChildren { get; set; }

    public bool IsSelectable => Folder is not null;

    public bool IsSubscribed => Folder?.Folder.IsSubscribed ?? false;

    public MailboxAccess Access => Mailbox?.Access ?? MailboxAccess.Read;

    /// <summary>The RFC 6154 attribute of a special folder (only in the personal namespace, so clients pick the user's own folders).</summary>
    public string? SpecialUse => !IsPersonal || Folder is null
        ? null
        : Folder.Kind switch
        {
            FolderKind.Sent => "\\Sent",
            FolderKind.Drafts => "\\Drafts",
            FolderKind.Trash => "\\Trash",
            FolderKind.Junk => "\\Junk",
            FolderKind.Archive => "\\Archive",
            _ => null,
        };

    /// <summary>The LIST attributes: \Noselect for grouping nodes, children information and the special use.</summary>
    public List<string> Attributes(bool subscribed)
    {
        var attributes = new List<string>(4);
        if (!IsSelectable)
        {
            attributes.Add("\\Noselect");
        }

        attributes.Add(HasChildren ? "\\HasChildren" : "\\HasNoChildren");
        if (SpecialUse is { } specialUse)
        {
            attributes.Add(specialUse);
        }

        if (subscribed && IsSubscribed)
        {
            attributes.Add("\\Subscribed");
        }

        return attributes;
    }
}

/// <summary>Where a new folder would go: the mailbox and its path as <see cref="FolderService"/> understands it.</summary>
internal sealed record ImapFolderTarget(AccessibleMailbox Mailbox, string FolderPath);

/// <summary>
/// The user's view of the mail store as an IMAP hierarchy (delimiter "/"): the folders of the own mailbox form the root ("INBOX",
/// "Drafts", "Archive/2025"), every other accessible mailbox (delegated, shared, "Unassigned") appears below
/// "Shared/&lt;mailbox name&gt;/".
/// </summary>
internal sealed class ImapMailboxTree
{
    public const char Delimiter = '/';
    public const string SharedRoot = "Shared";
    public const string SharedPrefix = SharedRoot + "/";

    private readonly Dictionary<string, ImapMailboxNode> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImapMailboxNode> _nodes = new();

    private ImapMailboxTree(AccessibleMailbox? personal) => Personal = personal;

    /// <summary>The user's own mailbox (null for users without one).</summary>
    public AccessibleMailbox? Personal { get; }

    public IReadOnlyList<ImapMailboxNode> Nodes => _nodes;

    /// <summary>Loads every accessible mailbox with its folders.</summary>
    public static Task<ImapMailboxTree> LoadAsync(ImapWork work, MailUser user, CancellationToken cancel)
        => LoadAsync(work, user, null, cancel);

    /// <summary>
    /// Loads only what is needed to resolve <paramref name="name"/>: the folders of the mailbox the name belongs to (cheaper for
    /// SELECT, STATUS, APPEND and friends).
    /// </summary>
    public static async Task<ImapMailboxTree> LoadAsync(ImapWork work, MailUser user, string? name, CancellationToken cancel)
    {
        IReadOnlyList<AccessibleMailbox> mailboxes = await work.Access.GetMailboxesAsync(user, cancel);
        AccessibleMailbox? personal = mailboxes.FirstOrDefault(m => m.IsOwn && m.Mailbox.Type == MailboxType.Personal)
                                      ?? mailboxes.FirstOrDefault(m => m.IsOwn);
        var tree = new ImapMailboxTree(personal);
        string? wanted = name is null ? null : Normalize(name);
        bool wantsShared = wanted is null || IsShared(wanted);

        if (personal is not null && (wanted is null || !IsShared(wanted)))
        {
            tree.AddFolders(personal, string.Empty, true, await work.Folders.ListAsync(personal.Mailbox.Id, cancel));
        }

        // A stable order, so a mailbox keeps its label from session to session even when two mailboxes share a name.
        List<AccessibleMailbox> others = mailboxes.Where(m => !ReferenceEquals(m, personal))
            .OrderBy(m => m.Mailbox.Type).ThenBy(m => m.Mailbox.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Mailbox.Id)
            .ToList();
        if (others.Count > 0 && wantsShared)
        {
            tree.Add(new ImapMailboxNode { Name = SharedRoot });
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AccessibleMailbox mailbox in others)
            {
                string prefix = SharedPrefix + UniqueLabel(mailbox.Mailbox, labels);
                tree.Add(new ImapMailboxNode { Name = prefix, Mailbox = mailbox });
                if (wanted is null || wanted.Equals(prefix, StringComparison.OrdinalIgnoreCase) || wanted.StartsWith(prefix + Delimiter, StringComparison.OrdinalIgnoreCase))
                {
                    tree.AddFolders(mailbox, prefix + Delimiter, false, await work.Folders.ListAsync(mailbox.Mailbox.Id, cancel));
                }
            }
        }

        tree.ComputeChildren();
        return tree;
    }

    /// <summary>The node with this name (case-insensitive; a trailing delimiter is ignored), or null.</summary>
    public ImapMailboxNode? Find(string name) => _byName.GetValueOrDefault(Normalize(name));

    /// <summary>Unicode normalisation (NFC) and no trailing delimiter, so names compare the same however a client spelled them.</summary>
    public static string Normalize(string name)
    {
        string trimmed = name.Length > 1 ? name.TrimEnd(Delimiter) : name;
        return trimmed.Normalize(NormalizationForm.FormC);
    }

    public static bool IsShared(string name)
        => name.Equals(SharedRoot, StringComparison.OrdinalIgnoreCase) || name.StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a folder with this name would be created: the mailbox and the path below it, with the existing parent folders spelled
    /// as they are stored. Null when the name does not lie inside a mailbox (e.g. "Shared" or "Shared/Unknown/X").
    /// </summary>
    public ImapFolderTarget? ResolveTarget(string name)
    {
        string normalized = Normalize(name);
        string[] parts = normalized.Split(Delimiter);
        AccessibleMailbox? mailbox;
        int first;

        if (IsShared(normalized))
        {
            if (parts.Length < 3 || Find(SharedPrefix + parts[1]) is not { Mailbox: { } shared })
            {
                return null;
            }

            mailbox = shared;
            first = 2;
        }
        else
        {
            mailbox = Personal;
            first = 0;
        }

        if (mailbox is null || parts.Skip(first).Any(p => p.Length == 0))
        {
            return null;
        }

        // Reuse the stored spelling of the deepest existing parent ("INBOX" may be stored under another name).
        for (int length = parts.Length - 1; length > first; length--)
        {
            ImapMailboxNode? parent = Find(string.Join(Delimiter, parts[..length]));
            if (parent?.Folder is not null && parent.Mailbox?.Mailbox.Id == mailbox.Mailbox.Id)
            {
                return new ImapFolderTarget(mailbox, parent.Folder.Path + Delimiter + string.Join(Delimiter, parts[length..]));
            }
        }

        return new ImapFolderTarget(mailbox, string.Join(Delimiter, parts[first..]));
    }

    /// <summary>
    /// A LIST pattern ("*" matches everything, "%" everything but the delimiter) as an anchored, case-insensitive regex; the pattern is
    /// NFC-normalised like the names it is matched against.
    /// </summary>
    public static Regex CompilePattern(string pattern)
    {
        var regex = new StringBuilder("^");
        foreach (char character in pattern.Normalize(NormalizationForm.FormC))
        {
            regex.Append(character switch
            {
                '*' => ".*",
                '%' => "[^/]*",
                _ => Regex.Escape(character.ToString()),
            });
        }

        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private void AddFolders(AccessibleMailbox mailbox, string prefix, bool isPersonal, IReadOnlyList<FolderInfo> folders)
    {
        Dictionary<long, FolderInfo> byId = folders.ToDictionary(f => f.Id);
        foreach (FolderInfo folder in folders)
        {
            string name = (prefix + RelativeName(folder, byId)).Normalize(NormalizationForm.FormC);
            if (isPersonal && IsShared(name))
            {
                // A personal folder called "Shared" would collide with the shared namespace; it stays reachable in the web client.
                continue;
            }

            Add(new ImapMailboxNode { Name = name, Folder = folder, Mailbox = mailbox, IsPersonal = isPersonal });
        }
    }

    private void Add(ImapMailboxNode node)
    {
        string key = Normalize(node.Name);
        if (_byName.TryAdd(key, node))
        {
            _nodes.Add(node);
        }
    }

    private void ComputeChildren()
    {
        foreach (ImapMailboxNode node in _nodes)
        {
            int slash = node.Name.LastIndexOf(Delimiter);
            if (slash > 0 && _byName.TryGetValue(Normalize(node.Name[..slash]), out ImapMailboxNode? parent))
            {
                parent.HasChildren = true;
            }
        }
    }

    /// <summary>The folder's name below its mailbox, built from the parent chain; the Inbox is always "INBOX".</summary>
    private static string RelativeName(FolderInfo folder, Dictionary<long, FolderInfo> byId)
    {
        var parts = new Stack<string>();
        for (FolderInfo? current = folder; current is not null; current = current.Folder.ParentId is long parentId ? byId.GetValueOrDefault(parentId) : null)
        {
            bool isRootInbox = current.Kind == FolderKind.Inbox && current.Folder.ParentId is null;
            parts.Push(isRootInbox ? "INBOX" : current.Folder.Name);
            if (parts.Count > 64)
            {
                break;
            }
        }

        return string.Join(Delimiter, parts);
    }

    /// <summary>The label of a mailbox below "Shared/": its name without delimiters, made unique.</summary>
    private static string UniqueLabel(Mailbox mailbox, HashSet<string> used)
    {
        string label = mailbox.Name.Replace(Delimiter, '-').Trim().Normalize(NormalizationForm.FormC);
        if (label.Length == 0)
        {
            label = "Mailbox";
        }

        string candidate = label;
        for (int counter = 2; !used.Add(candidate); counter++)
        {
            candidate = $"{label} ({counter})";
        }

        return candidate;
    }
}

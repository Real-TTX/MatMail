using System.Text.RegularExpressions;
using MatMail.Data;
using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailServer.Imap;

/// <summary>Mailbox commands: LIST (with LIST-EXTENDED and LIST-STATUS), LSUB, STATUS, SELECT/EXAMINE, CREATE, DELETE, RENAME, (UN)SUBSCRIBE.</summary>
internal sealed partial class ImapSession
{
    private static readonly HashSet<string> StatusItems = new(StringComparer.OrdinalIgnoreCase) { "MESSAGES", "RECENT", "UIDNEXT", "UIDVALIDITY", "UNSEEN" };

    // ---------------------------------------------------------------------------------------------------------------
    // LIST / LSUB
    // ---------------------------------------------------------------------------------------------------------------

    private async Task ListAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        var options = new ListOptions();
        if (parser.Peek() == '(')
        {
            ReadSelectionOptions(parser, options);
            parser.ExpectSpace();
        }

        string reference = ModifiedUtf7.Decode(parser.ReadAString());
        parser.ExpectSpace();
        List<string> patterns = ReadListPatterns(parser);
        if (parser.TrySpace() && !parser.AtEnd)
        {
            if (!parser.TryReadWord("RETURN"))
            {
                throw new ImapSyntaxException("Expected RETURN options.");
            }

            parser.ExpectSpace();
            ReadReturnOptions(parser, options);
        }

        parser.ExpectEnd();

        if (patterns.Count == 1 && patterns[0].Length == 0)
        {
            // The hierarchy delimiter and the root of the reference (RFC 3501, section 6.3.8).
            string root = ImapMailboxTree.IsShared(reference) ? ImapMailboxTree.SharedPrefix : string.Empty;
            WriteUntagged($"LIST (\\Noselect) \"/\" {ImapFormat.Mailbox(root)}");
            await CompleteAsync(command, "LIST completed");
            return;
        }

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, _shutdown);
        List<Regex> matchers = patterns.Select(p => ImapMailboxTree.CompilePattern(reference + p)).ToList();
        List<(ImapMailboxNode Node, bool ChildInfo)> results = SelectListResults(tree, options, node => matchers.Any(m => m.IsMatch(node.Name)));

        Dictionary<long, FolderStatus> statuses = options.ReturnStatus is null
            ? new Dictionary<long, FolderStatus>()
            : await LoadStatusAsync(work, results.Where(r => r.Node.IsSelectable).Select(r => r.Node.Folder!.Id).ToList());

        bool showSubscribed = options.Subscribed || options.ReturnSubscribed;
        foreach ((ImapMailboxNode node, bool childInfo) in results)
        {
            string attributes = string.Join(' ', node.Attributes(showSubscribed));
            string extended = childInfo ? $" (\"CHILDINFO\" ({options.ChildInfo}))" : string.Empty;
            WriteUntagged($"LIST ({attributes}) \"/\" {ImapFormat.Mailbox(node.Name)}{extended}");
            if (options.ReturnStatus is not null && node.IsSelectable && statuses.TryGetValue(node.Folder!.Id, out FolderStatus? status))
            {
                WriteUntagged($"STATUS {ImapFormat.Mailbox(node.Name)} ({FormatStatus(status, options.ReturnStatus)})");
            }
        }

        await CompleteAsync(command, "LIST completed", work);
    }

    /// <summary>
    /// Applies the selection options (RFC 5258): without options every matching name; SUBSCRIBED / SPECIAL-USE restrict the result;
    /// RECURSIVEMATCH adds parents whose matching descendants are not listed themselves, marked with CHILDINFO.
    /// </summary>
    private static List<(ImapMailboxNode Node, bool ChildInfo)> SelectListResults(ImapMailboxTree tree, ListOptions options, Func<ImapMailboxNode, bool> matches)
    {
        bool Satisfies(ImapMailboxNode node) => (!options.Subscribed || node.IsSubscribed) && (!options.SpecialUseOnly || node.SpecialUse is not null);

        var results = new List<(ImapMailboxNode, bool)>();
        foreach (ImapMailboxNode node in tree.Nodes.Where(matches))
        {
            bool satisfied = Satisfies(node);
            if (!options.RecursiveMatch)
            {
                if (satisfied)
                {
                    results.Add((node, false));
                }

                continue;
            }

            List<ImapMailboxNode> selectedDescendants = Descendants(tree, node).Where(Satisfies).ToList();
            if (satisfied)
            {
                results.Add((node, selectedDescendants.Count > 0));
            }
            else if (selectedDescendants.Any(d => !matches(d)))
            {
                results.Add((node, true));
            }
        }

        return results;
    }

    private static IEnumerable<ImapMailboxNode> Descendants(ImapMailboxTree tree, ImapMailboxNode node)
    {
        string prefix = node.Name + ImapMailboxTree.Delimiter;
        return tree.Nodes.Where(n => n.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadSelectionOptions(ImapParser parser, ListOptions options)
    {
        foreach (string option in parser.ReadAtomList())
        {
            switch (option.ToUpperInvariant())
            {
                case "SUBSCRIBED":
                    options.Subscribed = true;
                    break;
                case "REMOTE":
                    // There are no remote mailboxes; the option changes nothing.
                    break;
                case "RECURSIVEMATCH":
                    options.RecursiveMatch = true;
                    break;
                case "SPECIAL-USE":
                    options.SpecialUseOnly = true;
                    break;
                default:
                    throw new ImapSyntaxException($"Unknown LIST selection option {option}");
            }
        }

        if (options.RecursiveMatch && !options.Subscribed && !options.SpecialUseOnly)
        {
            throw new ImapSyntaxException("RECURSIVEMATCH needs another selection option");
        }
    }

    private static void ReadReturnOptions(ImapParser parser, ListOptions options)
    {
        parser.Expect('(');
        parser.TrySpace();
        while (!parser.TryConsume(')'))
        {
            string option = parser.ReadAtom().ToUpperInvariant();
            switch (option)
            {
                case "SUBSCRIBED":
                    options.ReturnSubscribed = true;
                    break;
                case "CHILDREN" or "SPECIAL-USE":
                    // Children and special-use attributes are always returned.
                    break;
                case "STATUS":
                    parser.ExpectSpace();
                    options.ReturnStatus = ReadStatusItems(parser);
                    break;
                default:
                    throw new ImapSyntaxException($"Unknown LIST return option {option}");
            }

            if (parser.Peek() != ')')
            {
                parser.ExpectSpace();
            }
        }
    }

    /// <summary>mbox-or-pat = list-mailbox / "(" list-mailbox *(SP list-mailbox) ")".</summary>
    private static List<string> ReadListPatterns(ImapParser parser)
    {
        if (!parser.TryConsume('('))
        {
            return new List<string> { parser.ReadListMailbox() };
        }

        var patterns = new List<string>();
        parser.TrySpace();
        while (!parser.TryConsume(')'))
        {
            patterns.Add(parser.ReadListMailbox());
            if (parser.Peek() != ')')
            {
                parser.ExpectSpace();
            }
        }

        if (patterns.Count == 0)
        {
            throw new ImapSyntaxException("Expected at least one mailbox pattern.");
        }

        return patterns;
    }

    /// <summary>
    /// LSUB (RFC 3501, section 6.3.9): the subscribed names; with "%" a parent that is not subscribed itself but has subscribed
    /// children is returned as \Noselect.
    /// </summary>
    private async Task LsubAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string reference = ModifiedUtf7.Decode(parser.ReadAString());
        parser.ExpectSpace();
        string pattern = parser.ReadListMailbox();
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, _shutdown);
        Regex matcher = ImapMailboxTree.CompilePattern(reference + pattern);
        bool percent = pattern.Contains('%');

        foreach (ImapMailboxNode node in tree.Nodes.Where(n => matcher.IsMatch(n.Name)))
        {
            if (node.IsSubscribed)
            {
                WriteUntagged($"LSUB ({string.Join(' ', node.Attributes(subscribed: false))}) \"/\" {ImapFormat.Mailbox(node.Name)}");
            }
            else if (percent && Descendants(tree, node).Any(d => d.IsSubscribed))
            {
                WriteUntagged($"LSUB (\\Noselect) \"/\" {ImapFormat.Mailbox(node.Name)}");
            }
        }

        await CompleteAsync(command, "LSUB completed", work);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // STATUS
    // ---------------------------------------------------------------------------------------------------------------

    private async Task StatusAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = parser.ReadMailbox();
        parser.ExpectSpace();
        List<string> items = ReadStatusItems(parser);
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxNode node = await FindSelectableAsync(work, name);
        Dictionary<long, FolderStatus> statuses = await LoadStatusAsync(work, new[] { node.Folder!.Id });
        FolderStatus status = statuses.GetValueOrDefault(node.Folder.Id) ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");
        WriteUntagged($"STATUS {ImapFormat.Mailbox(node.Name)} ({FormatStatus(status, items)})");
        await CompleteAsync(command, "STATUS completed", work);
    }

    private static List<string> ReadStatusItems(ImapParser parser)
    {
        List<string> items = parser.ReadAtomList();
        if (items.Count == 0 || items.Any(i => !StatusItems.Contains(i)))
        {
            throw new ImapSyntaxException("Invalid STATUS items");
        }

        return items.Select(i => i.ToUpperInvariant()).Distinct().ToList();
    }

    /// <summary>Message counts, UIDNEXT and UIDVALIDITY of folders, two queries for any number of folders.</summary>
    private async Task<Dictionary<long, FolderStatus>> LoadStatusAsync(ImapWork work, IReadOnlyCollection<long> folderIds)
    {
        if (folderIds.Count == 0)
        {
            return new Dictionary<long, FolderStatus>();
        }

        var folders = await work.Db.MailFolders.AsNoTracking()
            .Where(f => folderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UidNext, f.UidValidity })
            .ToListAsync(_shutdown);
        var counts = await work.Db.MailMessages.AsNoTracking()
            .Where(m => folderIds.Contains(m.FolderId))
            .GroupBy(m => m.FolderId)
            .Select(g => new { FolderId = g.Key, Total = g.Count(), Unseen = g.Count(m => !m.IsRead) })
            .ToListAsync(_shutdown);

        return folders.ToDictionary(
            f => f.Id,
            f =>
            {
                var count = counts.FirstOrDefault(c => c.FolderId == f.Id);
                return new FolderStatus(count?.Total ?? 0, count?.Unseen ?? 0, f.UidNext, f.UidValidity);
            });
    }

    private static string FormatStatus(FolderStatus status, IEnumerable<string> items)
        => string.Join(' ', items.Select(item => item + " " + item switch
        {
            "MESSAGES" => status.Messages,
            "UIDNEXT" => status.UidNext,
            "UIDVALIDITY" => status.UidValidity,
            "UNSEEN" => status.Unseen,
            _ => 0L,
        }));

    // ---------------------------------------------------------------------------------------------------------------
    // SELECT / EXAMINE
    // ---------------------------------------------------------------------------------------------------------------

    private async Task SelectAsync(ImapCommand command, bool examine)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = parser.ReadMailbox();
        parser.ExpectEnd();

        // SELECT always leaves the current mailbox, even when it fails (without expunging).
        _selection = null;
        _state = ImapSessionState.Authenticated;

        await using ImapWork work = OpenWork();
        ImapMailboxNode node = await FindSelectableAsync(work, name);
        var folder = await work.Db.MailFolders.AsNoTracking()
            .Where(f => f.Id == node.Folder!.Id)
            .Select(f => new { f.UidValidity, f.UidNext, f.ModSeq })
            .FirstOrDefaultAsync(_shutdown)
            ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");

        // Listen before loading, so a change during the load is not missed (it only causes one more comparison later).
        var selection = new ImapSelection(node, examine, folder.UidValidity) { KnownModSeq = folder.ModSeq };
        Interlocked.Exchange(ref _changePending, 0);
        _selection = selection;
        try
        {
            List<ImapMessageRow> rows = await LoadRowsAsync(work, selection.FolderId);
            selection.Messages.AddRange(rows.Select(r => r.ToMessage()));
        }
        catch
        {
            _selection = null;
            throw;
        }

        selection.ClientCount = selection.Count;
        foreach (string keyword in selection.Messages.SelectMany(m => m.Keywords))
        {
            selection.Keywords.Add(keyword);
        }

        WriteFlagResponses(selection);
        WriteUntagged($"{selection.Count} EXISTS");
        WriteUntagged("0 RECENT");
        int firstUnseen = selection.Messages.FindIndex(m => (m.Flags & ImapFlags.Seen) == 0);
        if (firstUnseen >= 0)
        {
            WriteUntagged($"OK [UNSEEN {firstUnseen + 1}] Message {firstUnseen + 1} is the first unseen");
        }

        WriteUntagged($"OK [UIDVALIDITY {selection.UidValidity}] UIDs valid");
        WriteUntagged($"OK [UIDNEXT {Math.Max(folder.UidNext, selection.MaxUid + 1)}] Predicted next UID");

        _state = ImapSessionState.Selected;
        string access = selection.IsReadOnly ? "READ-ONLY" : "READ-WRITE";
        Tagged(command, "OK", $"[{access}] {(examine ? "EXAMINE" : "SELECT")} completed");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // CREATE / DELETE / RENAME / SUBSCRIBE
    // ---------------------------------------------------------------------------------------------------------------

    private async Task CreateAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = ImapMailboxTree.Normalize(parser.ReadMailbox());
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, _shutdown);
        if (tree.Find(name) is not null || name.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImapNoException("[ALREADYEXISTS] Mailbox already exists");
        }

        ImapFolderTarget target = tree.ResolveTarget(name)
            ?? throw new ImapNoException(ImapMailboxTree.IsShared(name)
                ? "[CANNOT] Folders can only be created inside a shared mailbox (Shared/<mailbox>/<folder>)"
                : "[CANNOT] This name cannot be used for a folder");
        if (target.Mailbox.Access < MailboxAccess.Manage)
        {
            throw new ImapNoException("[NOPERM] You may not create folders in this mailbox");
        }

        (MailFolder? created, string? error) = await work.Folders.CreateAsync(target.Mailbox.Mailbox.Id, target.FolderPath, _shutdown);
        if (created is null)
        {
            throw new ImapNoException("[CANNOT] " + (error ?? "The folder could not be created"));
        }

        await CompleteAsync(command, "CREATE completed", work);
    }

    private async Task DeleteAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = parser.ReadMailbox();
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, _shutdown);
        ImapMailboxNode node = tree.Find(name) ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");
        if (!node.IsSelectable)
        {
            throw new ImapNoException("[CANNOT] This name cannot be deleted");
        }

        if (node.Access < MailboxAccess.Manage)
        {
            throw new ImapNoException("[NOPERM] You may not delete folders in this mailbox");
        }

        if (node.Folder!.Kind != FolderKind.Custom)
        {
            throw new ImapNoException("[CANNOT] System folders cannot be deleted");
        }

        if (node.HasChildren)
        {
            // RFC 3501 would keep the children below a \Noselect name; the store has no such names, so refuse instead of losing them.
            throw new ImapNoException("[CANNOT] The folder has subfolders; delete them first");
        }

        string? error = await work.Folders.DeleteAsync(node.Folder.Id, _shutdown);
        if (error is not null)
        {
            throw new ImapNoException("[CANNOT] " + error);
        }

        if (_selection?.FolderId == node.Folder.Id)
        {
            _selection = null;
            _state = ImapSessionState.Authenticated;
        }

        await CompleteAsync(command, "DELETE completed", work);
    }

    private async Task RenameAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string oldName = parser.ReadMailbox();
        parser.ExpectSpace();
        string newName = ImapMailboxTree.Normalize(parser.ReadMailbox());
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, _shutdown);
        ImapMailboxNode source = tree.Find(oldName) ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");
        if (!source.IsSelectable)
        {
            throw new ImapNoException("[CANNOT] This name cannot be renamed");
        }

        if (tree.Find(newName) is not null)
        {
            throw new ImapNoException("[ALREADYEXISTS] The new name already exists");
        }

        ImapFolderTarget target = tree.ResolveTarget(newName) ?? throw new ImapNoException("[CANNOT] This name cannot be used for a folder");
        if (target.Mailbox.Mailbox.Id != source.Folder!.Folder.MailboxId)
        {
            throw new ImapNoException("[CANNOT] Folders can only be renamed within their mailbox");
        }

        if (source.Access < MailboxAccess.Manage)
        {
            throw new ImapNoException("[NOPERM] You may not rename folders in this mailbox");
        }

        if (source.Folder.Kind == FolderKind.Inbox)
        {
            await RenameInboxAsync(work, source, target);
        }
        else
        {
            string? error = await work.Folders.RenameAsync(source.Folder.Id, target.FolderPath, _shutdown);
            if (error is not null)
            {
                throw new ImapNoException("[CANNOT] " + error);
            }
        }

        await CompleteAsync(command, "RENAME completed", work);
    }

    /// <summary>Renaming INBOX moves its messages into a new folder and leaves INBOX empty (RFC 3501, section 6.3.5).</summary>
    private async Task RenameInboxAsync(ImapWork work, ImapMailboxNode inbox, ImapFolderTarget target)
    {
        (MailFolder? created, string? error) = await work.Folders.CreateAsync(target.Mailbox.Mailbox.Id, target.FolderPath, _shutdown);
        if (created is null)
        {
            throw new ImapNoException("[CANNOT] " + (error ?? "The folder could not be created"));
        }

        long inboxId = inbox.Folder!.Id;
        List<long> ids = await work.Db.MailMessages.AsNoTracking().Where(m => m.FolderId == inboxId).OrderBy(m => m.Uid).Select(m => m.Id).ToListAsync(_shutdown);
        if (ids.Count > 0)
        {
            await work.Store.MoveAsync(ids, created.Id, _shutdown);
        }
    }

    private async Task SubscribeAsync(ImapCommand command, bool subscribe)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = parser.ReadMailbox();
        parser.ExpectEnd();

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, name, _shutdown);
        ImapMailboxNode node = tree.Find(name) ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");
        if (node.IsSelectable)
        {
            // The subscription is stored with the folder, so in a shared mailbox it applies to everybody: only managers change it.
            if (node.Access < MailboxAccess.Manage)
            {
                throw new ImapNoException("[NOPERM] Subscriptions of this shared mailbox are managed by its owner");
            }

            await work.Folders.SetSubscribedAsync(node.Folder!.Id, subscribe, _shutdown);
        }

        await CompleteAsync(command, (subscribe ? "SUBSCRIBE" : "UNSUBSCRIBE") + " completed", work);
    }

    /// <summary>Finds a mailbox the user may open; NO [NONEXISTENT] / [CANNOT] otherwise.</summary>
    private async Task<ImapMailboxNode> FindSelectableAsync(ImapWork work, string name)
    {
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, name, _shutdown);
        ImapMailboxNode node = tree.Find(name) ?? throw new ImapNoException("[NONEXISTENT] No such mailbox");
        return node.IsSelectable ? node : throw new ImapNoException("[CANNOT] This name cannot be selected");
    }

    private sealed record FolderStatus(long Messages, long Unseen, long UidNext, long UidValidity);

    /// <summary>The options of an extended LIST command.</summary>
    private sealed class ListOptions
    {
        public bool Subscribed { get; set; }

        public bool SpecialUseOnly { get; set; }

        public bool RecursiveMatch { get; set; }

        public bool ReturnSubscribed { get; set; }

        public List<string>? ReturnStatus { get; set; }

        /// <summary>The CHILDINFO extended data: the selection criteria the children satisfy.</summary>
        public string ChildInfo => string.Join(' ', new[] { Subscribed ? "\"SUBSCRIBED\"" : null, SpecialUseOnly ? "\"SPECIAL-USE\"" : null }.Where(s => s is not null));
    }
}

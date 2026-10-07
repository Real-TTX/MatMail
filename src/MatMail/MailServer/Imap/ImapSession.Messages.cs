using MatMail.Data;
using MatMail.Messaging;
using Microsoft.EntityFrameworkCore;

namespace MatMail.MailServer.Imap;

/// <summary>Message commands: APPEND, CHECK, CLOSE, UNSELECT, EXPUNGE, STORE, COPY, MOVE, SEARCH.</summary>
internal sealed partial class ImapSession
{
    // ---------------------------------------------------------------------------------------------------------------
    // APPEND
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>APPEND mailbox [(flags)] ["date-time"] literal — answered with APPENDUID (RFC 4315).</summary>
    private async Task AppendAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        parser.ExpectSpace();
        string name = parser.ReadMailbox();
        parser.ExpectSpace();

        List<string> flags = new();
        if (parser.Peek() == '(')
        {
            flags = parser.ReadFlagList();
            parser.ExpectSpace();
        }

        DateTime? receivedDate = null;
        if (parser.Peek() == '"')
        {
            receivedDate = ImapFormat.TryParseDateTime(parser.ReadString(), out DateTime parsed) ? parsed : throw new ImapSyntaxException("Invalid date-time.");
            parser.ExpectSpace();
        }

        byte[] message = parser.ReadBytes();
        parser.ExpectEnd();
        (ImapFlags systemFlags, string[] keywords) = SplitFlags(flags);

        await using ImapWork work = OpenWork();
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, name, _shutdown);
        ImapMailboxNode node = tree.Find(name) ?? throw new ImapNoException("[TRYCREATE] No such mailbox");
        if (!node.IsSelectable)
        {
            throw new ImapNoException("[CANNOT] Messages cannot be stored here");
        }

        if (node.Access < MailboxAccess.Edit)
        {
            throw new ImapNoException("[NOPERM] You may not add messages to this mailbox");
        }

        if (message.Length == 0)
        {
            throw new ImapNoException("[CANNOT] An empty message cannot be stored");
        }

        MailMessage stored = await work.Store.AddAsync(node.Folder!.Id, new NewMessage(message)
        {
            ReceivedDate = receivedDate,
            IsRead = systemFlags.HasFlag(ImapFlags.Seen),
            IsAnswered = systemFlags.HasFlag(ImapFlags.Answered),
            IsStarred = systemFlags.HasFlag(ImapFlags.Flagged),
            IsDeleted = systemFlags.HasFlag(ImapFlags.Deleted),
            IsDraft = systemFlags.HasFlag(ImapFlags.Draft),
            IsForwarded = systemFlags.HasFlag(ImapFlags.Forwarded),
            Keywords = keywords,
        }, _shutdown);

        await CompleteAsync(command, $"[APPENDUID {node.Folder.Folder.UidValidity} {stored.Uid}] APPEND completed", work);
    }

    /// <summary>System flags and keywords of a flag list; "\Recent" is ignored, other unknown system flags are an error.</summary>
    private static (ImapFlags Flags, string[] Keywords) SplitFlags(IEnumerable<string> flags)
    {
        ImapFlags system = ImapFlags.None;
        var keywords = new List<string>();
        foreach (string flag in flags)
        {
            if (ImapFlagNames.TryParse(flag, out ImapFlags known))
            {
                system |= known;
            }
            else if (flag.Equals("\\Recent", StringComparison.OrdinalIgnoreCase))
            {
                // \Recent is managed by the server and cannot be set.
            }
            else if (flag.StartsWith('\\'))
            {
                throw new ImapSyntaxException($"Unknown system flag {flag}");
            }
            else if (!keywords.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                keywords.Add(flag);
            }
        }

        return (system, keywords.ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // CHECK / CLOSE / UNSELECT / EXPUNGE
    // ---------------------------------------------------------------------------------------------------------------

    private Task CheckAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        return CompleteAsync(command, "CHECK completed");
    }

    /// <summary>Removes the messages marked \Deleted (silently, no EXPUNGE responses) unless read-only, and leaves the mailbox.</summary>
    private async Task CloseAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        ImapSelection selection = RequireSelection();
        _selection = null;
        _state = ImapSessionState.Authenticated;
        if (!selection.IsReadOnly)
        {
            await using ImapWork work = OpenWork();
            await work.Store.ExpungeAsync(selection.FolderId, null, _shutdown);
        }

        Tagged(command, "OK", "CLOSE completed");
    }

    private Task UnselectAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        _selection = null;
        _state = ImapSessionState.Authenticated;
        Tagged(command, "OK", "UNSELECT completed");
        return Task.CompletedTask;
    }

    /// <summary>EXPUNGE, and UID EXPUNGE (RFC 4315) which only removes the given UIDs.</summary>
    private async Task ExpungeAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        ImapSelection selection = RequireSelection();
        List<long>? uids = null;
        if (command.IsUid)
        {
            parser.ExpectSpace();
            SequenceSet set = parser.ReadSequenceSet();
            uids = selection.ResolveUidSet(set).Select(i => selection.Messages[i].Uid).ToList();
        }

        parser.ExpectEnd();
        EnsureWritable(selection);

        await using ImapWork work = OpenWork();
        if (uids is null || uids.Count > 0)
        {
            await work.Store.ExpungeAsync(selection.FolderId, uids, _shutdown);
        }

        await CompleteAsync(command, command.DisplayName + " completed", work);
    }

    private static void EnsureWritable(ImapSelection selection)
    {
        if (selection.Access < MailboxAccess.Edit)
        {
            throw new ImapNoException("[NOPERM] You may only read this mailbox");
        }

        if (selection.IsExamine)
        {
            throw new ImapNoException("[READ-ONLY] The mailbox is open read-only");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // STORE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>STORE set [+|-]FLAGS[.SILENT] (flags).</summary>
    private async Task StoreAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        ImapSelection selection = RequireSelection();
        parser.ExpectSpace();
        SequenceSet set = parser.ReadSequenceSet();
        parser.ExpectSpace();

        char mode = parser.Peek() is '+' or '-' ? parser.Peek() : '=';
        if (mode != '=')
        {
            parser.Expect(mode);
        }

        string item = parser.ReadAtom().ToUpperInvariant();
        if (item is not ("FLAGS" or "FLAGS.SILENT"))
        {
            throw new ImapSyntaxException("Expected FLAGS, +FLAGS or -FLAGS.");
        }

        parser.ExpectSpace();
        List<string> flags = parser.ReadFlagsLoose();
        parser.ExpectEnd();

        List<int> indexes = command.IsUid ? selection.ResolveUidSet(set) : selection.ResolveSequenceSet(set);
        EnsureWritable(selection);
        (ImapFlags systemFlags, string[] keywords) = SplitFlags(flags);
        List<ImapMessage> messages = indexes.Select(i => selection.Messages[i]).Where(m => !m.IsExpunged).ToList();

        await using ImapWork work = OpenWork();
        if (messages.Count > 0)
        {
            await work.Store.ChangeFlagsAsync(messages.Select(m => m.Id), BuildFlagChange(mode, systemFlags, keywords), _shutdown);
            await ReloadFlagsAsync(work, selection, messages);
        }

        ReportNewKeywords(selection);
        if (item == "FLAGS")
        {
            foreach (ImapMessage message in messages)
            {
                WriteUntagged($"{selection.IndexOfUid(message.Uid) + 1} FETCH (UID {message.Uid} FLAGS {ImapFlagNames.Format(message.Flags, message.Keywords)})");
            }
        }

        bool skipped = messages.Count < indexes.Count;
        await CompleteAsync(command, (skipped ? "[EXPUNGEISSUED] Some messages were expunged; " : string.Empty) + command.DisplayName + " completed", work, allowExpunge: command.IsUid);
    }

    private static FlagChange BuildFlagChange(char mode, ImapFlags flags, string[] keywords)
    {
        bool? Value(ImapFlags flag) => mode switch
        {
            '+' => (flags & flag) != 0 ? true : null,
            '-' => (flags & flag) != 0 ? false : null,
            _ => (flags & flag) != 0,
        };

        return new FlagChange
        {
            IsRead = Value(ImapFlags.Seen),
            IsAnswered = Value(ImapFlags.Answered),
            IsStarred = Value(ImapFlags.Flagged),
            IsDeleted = Value(ImapFlags.Deleted),
            IsDraft = Value(ImapFlags.Draft),
            IsForwarded = Value(ImapFlags.Forwarded),
            AddKeywords = mode == '+' && keywords.Length > 0 ? keywords : null,
            RemoveKeywords = mode == '-' && keywords.Length > 0 ? keywords : null,
            SetKeywords = mode == '=' ? keywords : null,
        };
    }

    /// <summary>Reads the flags as stored after a change (other sessions may have changed them at the same time).</summary>
    private async Task ReloadFlagsAsync(ImapWork work, ImapSelection selection, IReadOnlyCollection<ImapMessage> messages)
    {
        long[] ids = messages.Select(m => m.Id).ToArray();
        long folderId = selection.FolderId;
        Dictionary<long, ImapMessageRow> rows = await work.Db.MailMessages.AsNoTracking()
            .Where(m => m.FolderId == folderId && ids.Contains(m.Id))
            .Select(m => new ImapMessageRow(m.Id, m.Uid, m.IsRead, m.IsAnswered, m.IsStarred, m.IsDeleted, m.IsDraft, m.IsForwarded, m.Keywords))
            .ToDictionaryAsync(r => r.Id, _shutdown);

        foreach (ImapMessage message in messages)
        {
            if (rows.TryGetValue(message.Id, out ImapMessageRow? row))
            {
                (message.Flags, message.Keywords) = row.Flags();
                selection.ChangedFlags.Remove(message);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // COPY / MOVE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>COPY set mailbox — answered with COPYUID (RFC 4315).</summary>
    private async Task CopyAsync(ImapCommand command)
    {
        (ImapSelection selection, List<ImapMessage> messages, string targetName) = ReadTransfer(command);

        await using ImapWork work = OpenWork();
        ImapMailboxNode target = await FindTransferTargetAsync(work, targetName);
        var sourceUids = new List<long>();
        var targetUids = new List<long>();
        foreach (ImapMessage message in messages)
        {
            // One message at a time: the UIDs pair up exactly and only one message body is in memory.
            IReadOnlyList<MailMessage> copies = await work.Store.CopyAsync(new[] { message.Id }, target.Folder!.Id, _shutdown);
            if (copies.Count == 1)
            {
                sourceUids.Add(message.Uid);
                targetUids.Add(copies[0].Uid);
            }
        }

        if (messages.Count > 0 && sourceUids.Count == 0)
        {
            throw new ImapNoException("[UNAVAILABLE] The messages could not be read");
        }

        string copyUid = sourceUids.Count == 0
            ? string.Empty
            : $"[COPYUID {target.Folder!.Folder.UidValidity} {SequenceSet.Format(sourceUids)} {SequenceSet.Format(targetUids)}] ";
        await CompleteAsync(command, copyUid + command.DisplayName + " completed", work);
    }

    /// <summary>
    /// MOVE set mailbox (RFC 6851): the untagged OK with COPYUID comes first, then the EXPUNGE responses of the moved messages.
    /// </summary>
    private async Task MoveAsync(ImapCommand command)
    {
        (ImapSelection selection, List<ImapMessage> messages, string targetName) = ReadTransfer(command);
        EnsureWritable(selection);

        await using ImapWork work = OpenWork();
        ImapMailboxNode target = await FindTransferTargetAsync(work, targetName);
        if (target.Folder!.Id == selection.FolderId)
        {
            throw new ImapNoException("[CANNOT] The messages are already in this mailbox");
        }

        Dictionary<long, ImapMessage> byId = messages.ToDictionary(m => m.Id);
        IReadOnlyList<MailMessage> moved = messages.Count == 0
            ? Array.Empty<MailMessage>()
            : await work.Store.MoveAsync(messages.Select(m => m.Id), target.Folder.Id, _shutdown);

        var pairs = moved.Where(m => byId.ContainsKey(m.Id)).Select(m => (Source: byId[m.Id], NewUid: m.Uid)).OrderBy(p => p.Source.Uid).ToList();
        if (pairs.Count > 0)
        {
            WriteUntagged($"OK [COPYUID {target.Folder.Folder.UidValidity} {SequenceSet.Format(pairs.Select(p => p.Source.Uid))} {SequenceSet.Format(pairs.Select(p => p.NewUid))}] Moved");
            foreach (var pair in pairs.OrderByDescending(p => p.Source.Uid))
            {
                int index = selection.IndexOfUid(pair.Source.Uid);
                WriteUntagged($"{index + 1} EXPUNGE");
                selection.Messages.RemoveAt(index);
                selection.ClientCount--;
            }
        }

        await CompleteAsync(command, command.DisplayName + " completed", work);
    }

    /// <summary>The common arguments of COPY and MOVE: the messages (those still there) and the target name.</summary>
    private (ImapSelection Selection, List<ImapMessage> Messages, string Target) ReadTransfer(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        ImapSelection selection = RequireSelection();
        parser.ExpectSpace();
        SequenceSet set = parser.ReadSequenceSet();
        parser.ExpectSpace();
        string target = parser.ReadMailbox();
        parser.ExpectEnd();

        List<int> indexes = command.IsUid ? selection.ResolveUidSet(set) : selection.ResolveSequenceSet(set);
        return (selection, indexes.Select(i => selection.Messages[i]).Where(m => !m.IsExpunged).ToList(), target);
    }

    private async Task<ImapMailboxNode> FindTransferTargetAsync(ImapWork work, string name)
    {
        ImapMailboxTree tree = await ImapMailboxTree.LoadAsync(work, User, name, _shutdown);
        ImapMailboxNode target = tree.Find(name) ?? throw new ImapNoException("[TRYCREATE] No such mailbox");
        if (!target.IsSelectable)
        {
            throw new ImapNoException("[CANNOT] Messages cannot be stored here");
        }

        return target.Access >= MailboxAccess.Edit ? target : throw new ImapNoException("[NOPERM] You may not add messages to this mailbox");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // SEARCH
    // ---------------------------------------------------------------------------------------------------------------

    private async Task SearchAsync(ImapCommand command)
    {
        ImapParser parser = command.Parser;
        ImapSelection selection = RequireSelection();
        parser.ExpectSpace();
        if (parser.TryReadWord("RETURN"))
        {
            throw new ImapSyntaxException("SEARCH RETURN (ESEARCH) is not supported");
        }

        (SearchKey key, string? charset) = ImapSearchParser.Parse(parser);
        parser.ExpectEnd();
        if (charset is not null && !ImapSearchParser.SupportedCharsets.Contains(charset, StringComparer.OrdinalIgnoreCase))
        {
            throw new ImapNoException("[BADCHARSET (UTF-8 US-ASCII)] The charset is not supported");
        }

        await using ImapWork work = OpenWork();
        List<int> indexes = await new ImapSearchEngine(work, selection).SearchAsync(key, _shutdown);
        IEnumerable<long> numbers = command.IsUid ? indexes.Select(i => selection.Messages[i].Uid) : indexes.Select(i => (long)i + 1);
        WriteUntagged("SEARCH" + string.Concat(numbers.Select(n => " " + n)));
        await CompleteAsync(command, command.DisplayName + " completed", work, allowExpunge: command.IsUid);
    }
}

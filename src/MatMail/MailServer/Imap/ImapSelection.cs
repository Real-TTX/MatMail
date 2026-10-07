using MatMail.Data;

namespace MatMail.MailServer.Imap;

/// <summary>One message as the session knows it: what the client has been told so far.</summary>
internal sealed class ImapMessage
{
    public ImapMessage(long id, long uid, ImapFlags flags, string[] keywords)
    {
        Id = id;
        Uid = uid;
        Flags = flags;
        Keywords = keywords;
    }

    /// <summary>The <see cref="MailMessage"/> id.</summary>
    public long Id { get; }

    public long Uid { get; }

    public ImapFlags Flags { get; set; }

    public string[] Keywords { get; set; }

    /// <summary>Removed by somebody else, but the client has not been told yet (EXPUNGE may not be sent during FETCH/STORE/SEARCH).</summary>
    public bool IsExpunged { get; set; }

    public bool HasSameFlags(ImapFlags flags, string[] keywords)
        => Flags == flags && Keywords.Length == keywords.Length && !Keywords.Except(keywords, StringComparer.OrdinalIgnoreCase).Any();
}

/// <summary>
/// The selected folder of a session: a snapshot of its messages in UID order (index + 1 = sequence number) that is brought up to
/// date at the points where the protocol allows telling the client about changes.
/// </summary>
internal sealed class ImapSelection
{
    public ImapSelection(ImapMailboxNode node, bool isExamine, long uidValidity)
    {
        FolderId = node.Folder!.Id;
        MailboxId = node.Folder.Folder.MailboxId;
        Name = node.Name;
        Access = node.Access;
        IsExamine = isExamine;
        UidValidity = uidValidity;
    }

    public long FolderId { get; }

    public long MailboxId { get; }

    public string Name { get; }

    public MailboxAccess Access { get; }

    public bool IsExamine { get; }

    public long UidValidity { get; }

    /// <summary>EXAMINE, or the user may only read this mailbox.</summary>
    public bool IsReadOnly => IsExamine || Access < MailboxAccess.Edit;

    public List<ImapMessage> Messages { get; } = new();

    /// <summary>How many messages the client believes the folder holds (last EXISTS minus the EXPUNGEs sent since).</summary>
    public int ClientCount { get; set; }

    /// <summary>Messages whose flags were changed elsewhere and not yet reported with an untagged FETCH.</summary>
    public HashSet<ImapMessage> ChangedFlags { get; } = new();

    /// <summary>The folder's change counter at the last synchronisation.</summary>
    public long KnownModSeq { get; set; }

    /// <summary>Keywords announced in the FLAGS response.</summary>
    public HashSet<string> Keywords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Messages.Count;

    public long MaxUid => Messages.Count == 0 ? 0 : Messages[^1].Uid;

    public int UnseenCount => Messages.Count(m => !m.IsExpunged && (m.Flags & ImapFlags.Seen) == 0);

    /// <summary>The index of the message with this UID, or -1.</summary>
    public int IndexOfUid(long uid)
    {
        int low = 0;
        int high = Messages.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            long current = Messages[middle].Uid;
            if (current == uid)
            {
                return middle;
            }

            if (current < uid)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>The first index whose UID is at least <paramref name="uid"/>.</summary>
    public int LowerBound(long uid)
    {
        int low = 0;
        int high = Messages.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (Messages[middle].Uid < uid)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// The indexes a set of sequence numbers refers to (ascending). Numbers beyond the mailbox are a client error (BAD); in an empty
    /// mailbox, where "*" has no value, every set is simply empty.
    /// </summary>
    public List<int> ResolveSequenceSet(SequenceSet set)
    {
        var indexes = new SortedSet<int>();
        if (Messages.Count == 0)
        {
            return new List<int>();
        }

        foreach ((long low, long high) in set.Resolve(Messages.Count))
        {
            if (low < 1 || high > Messages.Count)
            {
                throw new ImapSyntaxException("Invalid message sequence number.");
            }

            for (long number = low; number <= high; number++)
            {
                indexes.Add((int)number - 1);
            }
        }

        return indexes.ToList();
    }

    /// <summary>The indexes of the messages whose UIDs are in the set (ascending); unknown UIDs are ignored.</summary>
    public List<int> ResolveUidSet(SequenceSet set)
    {
        var indexes = new SortedSet<int>();
        if (Messages.Count == 0)
        {
            return new List<int>();
        }

        foreach ((long low, long high) in set.Resolve(MaxUid))
        {
            for (int index = LowerBound(low); index < Messages.Count && Messages[index].Uid <= high; index++)
            {
                indexes.Add(index);
            }
        }

        return indexes.ToList();
    }
}

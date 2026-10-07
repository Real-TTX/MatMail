namespace MatMail.MailServer.Imap;

/// <summary>
/// The last few messages a session loaded, with their parsed structure. Clients typically fetch BODYSTRUCTURE first and then the
/// parts they want in separate commands; this saves loading and parsing the same message again. Message contents never change.
/// </summary>
internal sealed class ImapMessageCache
{
    private const int Capacity = 3;
    private const int MaxCachedSize = 16 * 1024 * 1024;

    private readonly LinkedList<Entry> _entries = new();

    /// <summary>The raw message (null when it is not available, e.g. a provider that cannot be reached for a referenced message).</summary>
    public async Task<byte[]?> GetRawAsync(ImapWork work, long messageId, CancellationToken cancel)
    {
        Entry? hit = Find(messageId);
        if (hit is not null)
        {
            return hit.Raw;
        }

        byte[]? raw = await work.Store.GetRawAsync(messageId, cancel);
        if (raw is not null && raw.Length <= MaxCachedSize)
        {
            _entries.AddFirst(new Entry(messageId, raw));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        return raw;
    }

    public ImapMessageStructure GetStructure(long messageId, byte[] raw)
    {
        Entry? hit = Find(messageId);
        if (hit?.Structure is { } cached)
        {
            return cached;
        }

        ImapMessageStructure structure = ImapMessageStructure.Parse(raw);
        if (hit is not null)
        {
            hit.Structure = structure;
        }

        return structure;
    }

    private Entry? Find(long messageId)
    {
        for (LinkedListNode<Entry>? node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.MessageId == messageId)
            {
                _entries.Remove(node);
                _entries.AddFirst(node);
                return node.Value;
            }
        }

        return null;
    }

    private sealed class Entry
    {
        public Entry(long messageId, byte[] raw)
        {
            MessageId = messageId;
            Raw = raw;
        }

        public long MessageId { get; }

        public byte[] Raw { get; }

        public ImapMessageStructure? Structure { get; set; }
    }
}

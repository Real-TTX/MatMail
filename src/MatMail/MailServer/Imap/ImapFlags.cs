namespace MatMail.MailServer.Imap;

/// <summary>The message flags the store keeps as columns (keywords are kept separately).</summary>
[Flags]
internal enum ImapFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Deleted = 8,
    Draft = 16,

    /// <summary>The "$Forwarded" keyword, kept as <c>IsForwarded</c>.</summary>
    Forwarded = 32,
}

/// <summary>Names and conversions of IMAP flags.</summary>
internal static class ImapFlagNames
{
    public const string ForwardedKeyword = "$Forwarded";

    /// <summary>The flags every mailbox knows, in the order they are listed.</summary>
    public static readonly IReadOnlyList<(ImapFlags Flag, string Name)> Known = new[]
    {
        (ImapFlags.Answered, "\\Answered"),
        (ImapFlags.Flagged, "\\Flagged"),
        (ImapFlags.Deleted, "\\Deleted"),
        (ImapFlags.Seen, "\\Seen"),
        (ImapFlags.Draft, "\\Draft"),
        (ImapFlags.Forwarded, ForwardedKeyword),
    };

    public static ImapFlags From(bool isRead, bool isAnswered, bool isStarred, bool isDeleted, bool isDraft, bool isForwarded)
    {
        ImapFlags flags = ImapFlags.None;
        flags |= isRead ? ImapFlags.Seen : ImapFlags.None;
        flags |= isAnswered ? ImapFlags.Answered : ImapFlags.None;
        flags |= isStarred ? ImapFlags.Flagged : ImapFlags.None;
        flags |= isDeleted ? ImapFlags.Deleted : ImapFlags.None;
        flags |= isDraft ? ImapFlags.Draft : ImapFlags.None;
        flags |= isForwarded ? ImapFlags.Forwarded : ImapFlags.None;
        return flags;
    }

    /// <summary>A system flag ("\Seen", case-insensitive) or the "$Forwarded" keyword; false for other keywords.</summary>
    public static bool TryParse(string name, out ImapFlags flag)
    {
        foreach ((ImapFlags known, string knownName) in Known)
        {
            if (string.Equals(name, knownName, StringComparison.OrdinalIgnoreCase))
            {
                flag = known;
                return true;
            }
        }

        flag = ImapFlags.None;
        return false;
    }

    /// <summary>Keywords that can be shown over IMAP: valid atoms, without "$Forwarded" (that one is a column).</summary>
    public static string[] CleanKeywords(IEnumerable<string> keywords)
        => keywords
            .Where(k => k.Length > 0 && k.All(ImapParser.IsAtomChar) && !string.Equals(k, ForwardedKeyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>"(\Seen \Flagged $Forwarded Label)".</summary>
    public static string Format(ImapFlags flags, IReadOnlyCollection<string> keywords)
    {
        var parts = new List<string>(8);
        foreach ((ImapFlags flag, string name) in Known)
        {
            if ((flags & flag) != 0)
            {
                parts.Add(name);
            }
        }

        parts.AddRange(keywords);
        return "(" + string.Join(' ', parts) + ")";
    }
}

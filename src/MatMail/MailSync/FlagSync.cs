namespace MatMail.MailSync;

/// <summary>\Seen and \Flagged of a remote message.</summary>
public readonly record struct RemoteFlags(bool Seen, bool Flagged);

/// <summary>
/// One local copy as the flag comparison sees it. <see cref="SyncedDate"/> is when the copy and the provider were last known to
/// agree (the UpdateDate of the message's <c>RemoteMessageState</c> row, set at import and at every reconciliation).
/// </summary>
public sealed record LocalFlags(long MessageId, string RemoteUid, bool IsRead, bool IsStarred, DateTime UpdateDate, DateTime SyncedDate);

/// <summary>The provider changed the flags: the local copy takes them over.</summary>
public sealed record FlagPull(long MessageId, string RemoteUid, bool IsRead, bool IsStarred);

/// <summary>The local copy changed: the provider takes its flags over.</summary>
public sealed record FlagPush(string RemoteUid, bool Seen, bool Flagged, RemoteFlags Remote);

/// <summary>What the flag comparison of a folder decided. <see cref="InSync"/>: changed locally, but equal to the provider already.</summary>
public sealed record FlagPlan(IReadOnlyList<FlagPull> Pull, IReadOnlyList<FlagPush> Push, IReadOnlyList<string> InSync);

/// <summary>The two-way comparison of read/starred between local copies and the provider.</summary>
public static class FlagSync
{
    /// <summary>
    /// A copy changed after its last reconciliation wins and is pushed; otherwise the provider wins and is pulled. When both
    /// changed, the local change wins. Remote messages with several local copies (one per recipient mailbox) are left alone,
    /// because their flags belong to different people.
    /// </summary>
    public static FlagPlan Plan(IEnumerable<LocalFlags> local, IReadOnlyDictionary<string, RemoteFlags> remote)
    {
        var pull = new List<FlagPull>();
        var push = new List<FlagPush>();
        var inSync = new List<string>();

        foreach (IGrouping<string, LocalFlags> copies in local.GroupBy(l => l.RemoteUid, StringComparer.Ordinal))
        {
            LocalFlags[] list = copies.ToArray();
            if (list.Length != 1 || !remote.TryGetValue(copies.Key, out RemoteFlags flags))
            {
                continue;
            }

            LocalFlags copy = list[0];
            bool changedLocally = copy.UpdateDate > copy.SyncedDate;
            bool equal = copy.IsRead == flags.Seen && copy.IsStarred == flags.Flagged;
            if (equal)
            {
                if (changedLocally)
                {
                    inSync.Add(copy.RemoteUid);
                }

                continue;
            }

            if (changedLocally)
            {
                push.Add(new FlagPush(copy.RemoteUid, copy.IsRead, copy.IsStarred, flags));
            }
            else
            {
                pull.Add(new FlagPull(copy.MessageId, copy.RemoteUid, flags.Seen, flags.Flagged));
            }
        }

        return new FlagPlan(pull, push, inSync);
    }
}

namespace MatMail.Messaging;

public enum MailEventKind
{
    /// <summary>A message arrived in a folder.</summary>
    NewMessage,
    /// <summary>Flags of messages changed (read, starred, ...).</summary>
    FlagsChanged,
    /// <summary>Messages left a folder (deleted, moved).</summary>
    Removed,
    /// <summary>Folders were created, renamed or deleted.</summary>
    FoldersChanged,
}

/// <summary>Something happened in a mailbox. <c>MessageId</c> is set for single-message events.</summary>
public sealed record MailEvent(MailEventKind Kind, long TenantId, long MailboxId, long FolderId, long? MessageId = null);

/// <summary>
/// In-process notifications: the mail store publishes, the web client's live updates and IMAP IDLE sessions listen. Handlers must be
/// quick and must not throw (they run on the publisher's thread).
/// </summary>
public sealed class MailEventHub
{
    private readonly object _lock = new();
    private readonly List<Action<MailEvent>> _handlers = new();

    public void Publish(MailEvent mailEvent)
    {
        Action<MailEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = _handlers.ToArray();
        }

        foreach (Action<MailEvent> handler in snapshot)
        {
            try
            {
                handler(mailEvent);
            }
            catch (Exception)
            {
                // A faulty listener must never break the mail store.
            }
        }
    }

    /// <summary>Starts listening; dispose the result to stop.</summary>
    public IDisposable Subscribe(Action<MailEvent> handler)
    {
        lock (_lock)
        {
            _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<MailEvent> handler)
    {
        lock (_lock)
        {
            _handlers.Remove(handler);
        }
    }

    private sealed class Subscription(MailEventHub hub, Action<MailEvent> handler) : IDisposable
    {
        public void Dispose() => hub.Unsubscribe(handler);
    }
}

namespace MatMail.Messaging;

/// <summary>The outcome of a manual synchronisation of a connected account.</summary>
public sealed record SyncOutcome(bool Ok, string Message);

/// <summary>
/// What the admin pages need from the synchronisation engine: run an account now, or just ask for it to be next in line.
/// The engine (<c>MailSync</c>) registers the real implementation; without it this one says so.
/// </summary>
public interface IAccountSyncRunner
{
    /// <summary>Synchronises the account right now and reports the result.</summary>
    Task<SyncOutcome> SyncNowAsync(long accountId, CancellationToken cancel = default);

    /// <summary>Asks the scheduler to take this account next (returns immediately).</summary>
    void Request(long accountId);

    /// <summary>
    /// Makes the next run compare everything again: for when the account's server, user or role changed (UIDs of another server
    /// mean other messages). Returns false while a run is going.
    /// </summary>
    Task<bool> ResetAsync(long accountId, CancellationToken cancel = default);
}

internal sealed class UnavailableSyncRunner : IAccountSyncRunner
{
    public Task<SyncOutcome> SyncNowAsync(long accountId, CancellationToken cancel = default)
        => Task.FromResult(new SyncOutcome(false, "The synchronisation is not available in this installation."));

    public void Request(long accountId)
    {
    }

    public Task<bool> ResetAsync(long accountId, CancellationToken cancel = default) => Task.FromResult(true);
}

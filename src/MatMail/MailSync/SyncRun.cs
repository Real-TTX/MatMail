using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;

namespace MatMail.MailSync;

/// <summary>The outcome of one synchronisation run of a provider account (what the "Sync now" button shows).</summary>
public sealed record SyncReport
{
    public long AccountId { get; init; }

    /// <summary>False when the run did not start: unknown, disabled or send-only account, already running, synchronisation switched off.</summary>
    public bool Started { get; init; }

    /// <summary>The run ended without an account- or folder-level error (single messages may still have failed, see <see cref="Failed"/>).</summary>
    public bool Succeeded { get; init; }

    /// <summary>Messages stored locally (with live access: the metadata rows created).</summary>
    public int Downloaded { get; init; }

    public int DeletedAtProvider { get; init; }

    /// <summary>New remote messages that were not stored again: already present locally, or too large to download.</summary>
    public int Skipped { get; init; }

    /// <summary>Messages that could not be imported (retried in the next run, given up after a few attempts).</summary>
    public int Failed { get; init; }

    public int FlagChanges { get; init; }

    /// <summary>The per-run limit was reached; the account is due again at once.</summary>
    public bool MorePending { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Readable summary, e.g. "12 new messages", or the reason of the failure.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The run did not start because another one of the same account was going (that one brings the mail in).</summary>
    public bool AlreadyRunning { get; init; }

    public static SyncReport NotStarted(long accountId, string message, bool alreadyRunning = false)
        => new() { AccountId = accountId, Message = message, AlreadyRunning = alreadyRunning };
}

/// <summary>A setting prevents the synchronisation (no target mailbox, a server without UIDL, ...); the message is shown as it is.</summary>
public sealed class SyncConfigurationException(string message) : Exception(message);

/// <summary>Fetches the mail of one provider account over one protocol.</summary>
public interface IAccountSync
{
    Task RunAsync(SyncRun run, CancellationToken cancel);
}

/// <summary>One run of one account: the settings that really apply and what happened so far.</summary>
public sealed class SyncRun
{
    private readonly MailSyncOptions _options;
    private readonly List<string> _notes = new();

    public SyncRun(MailAccount account, MailSyncOptions options)
    {
        Account = account;
        _options = options;
        Retention = EffectiveRetention(account, out string? note);
        if (note is not null)
        {
            Note(note);
        }
    }

    public MailAccount Account { get; }

    public MailAccountRole Role => Account.Role;

    /// <summary>The retention that really applies: backups never delete, live access needs IMAP and the everyday-mail role.</summary>
    public ServerRetention Retention { get; }

    public bool DeletesAtProvider => Retention == ServerRetention.DeleteAfterDownload;

    public bool IsLiveAccess => Retention == ServerRetention.LiveAccess;

    /// <summary>Flags are compared for IMAP accounts whose mail stays at the provider; never for backups.</summary>
    public bool SyncsFlags => Account.ReceiveProtocol == ReceiveProtocol.Imap && Role != MailAccountRole.Backup && !DeletesAtProvider;

    /// <summary>Backup/Migration: where the copies go. Mail: the fallback mailbox for unclaimed mail (null: it stays in Unassigned).</summary>
    public Mailbox? TargetMailbox { get; set; }

    public int Downloaded { get; set; }
    public int AlreadyPresent { get; set; }
    public int TooLarge { get; set; }
    public int Failed { get; set; }
    public int GivenUp { get; set; }
    public int DeletedAtProvider { get; set; }
    public int RemovedLocally { get; set; }
    public int FlagChanges { get; set; }
    public int FoldersSynced { get; set; }
    public bool MorePending { get; set; }
    public bool Interrupted { get; set; }

    /// <summary>Problems with single folders (missing, refused); the other folders are still synchronised.</summary>
    public List<string> FolderErrors { get; } = new();

    /// <summary>Remarks for the report, e.g. that a retention setting does not apply.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Messages handled so far; each counts against <see cref="MailSyncOptions.MaxMessagesPerRun"/>.</summary>
    public int Processed => Downloaded + AlreadyPresent + TooLarge + Failed + GivenUp;

    public int Remaining => Math.Max(0, _options.MaxMessagesPerRun - Processed);

    public void Note(string text)
    {
        if (!_notes.Contains(text))
        {
            _notes.Add(text);
        }
    }

    /// <summary>The retention that applies to an account, with the reason when it differs from the configured one.</summary>
    public static ServerRetention EffectiveRetention(MailAccount account, out string? note)
    {
        note = null;
        if (account.Role == MailAccountRole.Backup && account.Retention != ServerRetention.KeepOnServer)
        {
            note = "backup accounts never delete at the provider (the retention setting is ignored)";
            return ServerRetention.KeepOnServer;
        }

        if (account.Retention == ServerRetention.LiveAccess && account.ReceiveProtocol != ReceiveProtocol.Imap)
        {
            note = "live access needs IMAP; with POP3 the messages are downloaded and kept on the server";
            return ServerRetention.KeepOnServer;
        }

        if (account.Retention == ServerRetention.LiveAccess && account.Role != MailAccountRole.Mail)
        {
            note = "live access only applies to everyday mail; the messages are copied and kept on the server";
            return ServerRetention.KeepOnServer;
        }

        return account.Retention;
    }
}

/// <summary>When an account is due again.</summary>
public static class SyncSchedule
{
    public static TimeSpan IntervalOf(MailAccount account) => TimeSpan.FromMinutes(Math.Max(1, account.SyncIntervalMinutes));

    /// <summary>
    /// The pause after <paramref name="failureCount"/> failed runs in a row: the interval, then twice, four times, ... as long,
    /// up to <paramref name="maxBackoff"/> (or the interval itself when that is longer).
    /// </summary>
    public static TimeSpan BackoffAfter(int failureCount, TimeSpan interval, TimeSpan maxBackoff)
    {
        if (failureCount <= 1)
        {
            return interval;
        }

        TimeSpan cap = interval > maxBackoff ? interval : maxBackoff;
        double ticks = Math.Min(interval.Ticks * Math.Pow(2, Math.Min(failureCount - 1, 30)), cap.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>The next due date after a run: back-off after a complete failure, at once when more is waiting, else after the interval.</summary>
    public static DateTime NextSyncDate(DateTime now, TimeSpan interval, bool failedCompletely, int failureCount, bool morePending, TimeSpan maxBackoff)
    {
        if (failedCompletely)
        {
            return now + BackoffAfter(failureCount, interval, maxBackoff);
        }

        return morePending ? now : now + interval;
    }
}

/// <summary>The readable texts of a run (account list, activity log, "Sync now").</summary>
public static class SyncText
{
    /// <summary>What a run did, e.g. "12 new messages; 12 deleted at the provider".</summary>
    public static string Summary(SyncRun run, int maxAttempts)
    {
        var parts = new List<string> { run.Downloaded == 0 ? "No new messages" : Count(run.Downloaded, "new message", "new messages") };
        AddCount(parts, run.DeletedAtProvider, "deleted at the provider");
        AddCount(parts, run.AlreadyPresent, "already present");
        AddCount(parts, run.TooLarge, "too large to download");
        AddCount(parts, run.Failed, "could not be imported (retried in the next run)");
        AddCount(parts, run.GivenUp, $"given up after {maxAttempts} failed attempts");
        AddCount(parts, run.RemovedLocally, "removed (gone at the provider)");
        if (run.FlagChanges > 0)
        {
            parts.Add(Count(run.FlagChanges, "flag change", "flag changes"));
        }

        if (run.MorePending)
        {
            parts.Add("more follow in the next run");
        }

        if (run.Interrupted)
        {
            parts.Add("interrupted, continues with the next run");
        }

        parts.AddRange(run.Notes);
        return string.Join("; ", parts);
    }

    /// <summary>What went wrong: the account-level failure (readable, without credentials) and the folder problems.</summary>
    public static string Problem(SyncRun run, Exception? failure, string? host)
    {
        var problems = new List<string>();
        if (failure is not null)
        {
            problems.Add(failure is SyncConfigurationException or MailboxFullException ? failure.Message : ProviderConnector.Describe(failure, host));
        }

        problems.AddRange(run.FolderErrors);
        return string.Join("; ", problems);
    }

    private static string Count(int count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private static void AddCount(List<string> parts, int count, string text)
    {
        if (count > 0)
        {
            parts.Add($"{count} {text}");
        }
    }
}

/// <summary>
/// The position of a folder only moves over messages that are dealt with: after the first failure it stays put, so the failed
/// message (and everything after it) is looked at again in the next run. Messages after it that were stored are recognised then.
/// </summary>
public sealed class UidProgress
{
    public UidProgress(long start) => Position = start;

    public long Position { get; private set; }

    public bool Blocked { get; private set; }

    public void Complete(uint uid)
    {
        if (!Blocked && uid > Position)
        {
            Position = uid;
        }
    }

    public void Fail() => Blocked = true;
}

/// <summary>Splitting the new UIDs of a folder into batches.</summary>
public static class SyncBatches
{
    /// <summary>The UIDs above <paramref name="lastUid"/>, ascending and without duplicates, in batches of <paramref name="batchSize"/>.</summary>
    /// <remarks>"UID n:*" also returns the highest message when nothing is above n, so the list is filtered here.</remarks>
    public static List<uint[]> NewUids(IEnumerable<uint> found, long lastUid, int batchSize)
        => found.Where(uid => uid > lastUid).Distinct().Order().Chunk(Math.Max(1, batchSize)).ToList();
}

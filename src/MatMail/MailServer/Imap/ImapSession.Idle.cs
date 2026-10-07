using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>
/// IDLE (RFC 2177): the client waits for "DONE" while the server pushes EXISTS / EXPUNGE / FETCH as soon as the selected folder
/// changes. Changes arrive through the event hub; the folder's change counter is also checked periodically as a safety net.
/// </summary>
internal sealed partial class ImapSession
{
    /// <summary>A delivery often comes with several events (new message, flags); a short pause reports them together.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(50);

    private async Task IdleAsync(ImapCommand command)
    {
        command.Parser.ExpectEnd();
        WriteLine("+ idling");
        await _connection.FlushAsync(_shutdown);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        idle.CancelAfter(_context.IdleTimeout);
        Task<byte[]?> done = _connection.ReadLineAsync(ImapRequestReader.MaxLineLength, idle.Token);
        try
        {
            while (!await WaitForDoneOrChangeAsync(done, idle.Token))
            {
                await SynchronizeAsync(null, allowExpunge: true);
                if (IsClosing)
                {
                    ObserveLater(done);
                    return;
                }

                await _connection.FlushAsync(_shutdown);
            }

            byte[]? line = await done;
            if (line is null)
            {
                _state = ImapSessionState.Logout;
                return;
            }

            if (!Encoding.ASCII.GetString(line).Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase))
            {
                Tagged(command, "BAD", "Expected DONE");
                return;
            }

            await CompleteAsync(command, "IDLE terminated");
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            ObserveLater(done);
            await SayGoodbyeAsync("BYE Autologout; idle for too long");
        }
    }

    /// <summary>True when the client ended IDLE; false when the folder changed or the poll interval passed.</summary>
    private async Task<bool> WaitForDoneOrChangeAsync(Task<byte[]?> done, CancellationToken cancel)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<bool> wake = _wake.WaitAsync(_context.IdlePollInterval, stopWaiting.Token);
        Task first = await Task.WhenAny(done, wake);
        if (first == done)
        {
            // Cancel the wait so it does not swallow a wake-up meant for a later IDLE.
            await stopWaiting.CancelAsync();
            await Task.WhenAny(wake);
            return true;
        }

        if (await wake)
        {
            await Task.Delay(SettleDelay, cancel);
        }

        return false;
    }

    /// <summary>A read that is abandoned (the session ends) must not leave an unobserved exception behind.</summary>
    private static void ObserveLater(Task task)
        => task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

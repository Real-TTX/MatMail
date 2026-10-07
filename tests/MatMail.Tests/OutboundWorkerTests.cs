using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MatMail.Data;
using MatMail.MailServer.Outbound;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MatMail.Tests;

/// <summary>The rules of the outgoing queue, without a database.</summary>
public class OutboundScheduleTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(5, 720)]
    [InlineData(9, 720)]
    public void Retries_follow_the_configured_minutes_and_the_last_one_repeats(int attempts, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), OutboundSchedule.RetryDelay(attempts, new[] { 5, 15, 60, 240, 720 }));

    [Fact]
    public void Without_a_schedule_the_retry_waits_a_quarter_of_an_hour()
        => Assert.Equal(TimeSpan.FromMinutes(15), OutboundSchedule.RetryDelay(1, Array.Empty<int>()));

    [Fact]
    public void Delivered_recipients_leave_the_entry_and_temporary_ones_wait()
    {
        DateTime now = DateTime.UtcNow;
        var message = new OutboundMessage { Recipients = new[] { "a@x.test", "b@x.test", "c@x.test" }, CreateDate = now, Status = OutboundStatus.Sending };
        OutboundAttempt attempt = OutboundSchedule.Apply(message, new[]
        {
            new RecipientOutcome("a@x.test", RecipientState.Delivered, "250 ok"),
            new RecipientOutcome("b@x.test", RecipientState.PermanentFailure, "550 unknown"),
            new RecipientOutcome("c@x.test", RecipientState.TemporaryFailure, "451 later"),
        }, now, new Configuration.QueueConfig { RetryMinutes = new[] { 10 } });

        Assert.Equal(OutboundStatus.Pending, message.Status);
        Assert.Equal(new[] { "c@x.test" }, message.Recipients);
        Assert.Equal(now.AddMinutes(10), message.NextAttemptDate);
        Assert.Equal(1, message.AttemptCount);
        Assert.Equal("b@x.test", Assert.Single(attempt.Bounced).Address);
        Assert.Contains("c@x.test: 451 later", message.LastError);
    }

    [Fact]
    public void Temporary_failures_turn_into_bounces_after_the_maximum_age()
    {
        DateTime now = DateTime.UtcNow;
        var message = new OutboundMessage { Recipients = new[] { "c@x.test" }, CreateDate = now.AddHours(-73), Status = OutboundStatus.Sending };
        OutboundAttempt attempt = OutboundSchedule.Apply(
            message, new[] { new RecipientOutcome("c@x.test", RecipientState.TemporaryFailure, "451 later") }, now, new Configuration.QueueConfig { MaxAgeHours = 72 });

        Assert.True(attempt.Expired);
        Assert.Equal(OutboundStatus.Failed, message.Status);
        Assert.Equal(new[] { "c@x.test" }, message.Recipients);
        Assert.StartsWith("Given up after 72 hours.", message.LastError);
    }

    [Fact]
    public void Mx_hosts_are_tried_by_preference()
    {
        MxLookup lookup = DnsMxResolver.Order("example.test", new[] { (20, "backup.example.test."), (10, "mx1.example.test."), (10, "mx2.example.test.") });
        Assert.Null(lookup.Error);
        Assert.Equal(new[] { "mx1.example.test", "mx2.example.test" }, lookup.Hosts.Take(2).OrderBy(h => h));
        Assert.Equal("backup.example.test", lookup.Hosts[2]);
    }

    [Fact]
    public async Task A_domain_name_that_dns_cannot_carry_fails_for_good()
    {
        // Valid for the address syntax, but the label is longer than 63 octets once encoded (punycode).
        MxLookup lookup = await new DnsMxResolver().ResolveAsync(new string('ä', 60) + ".test", CancellationToken.None);
        Assert.True(lookup.IsPermanent);
        Assert.Empty(lookup.Hosts);
    }

    [Fact]
    public void A_domain_without_mx_is_its_own_mail_server_and_a_null_mx_takes_no_mail()
    {
        Assert.Equal(new[] { "example.test" }, DnsMxResolver.Order("example.test", Array.Empty<(int, string)>()).Hosts);

        MxLookup nullMx = DnsMxResolver.Order("example.test", new[] { (0, ".") });
        Assert.True(nullMx.IsPermanent);
        Assert.NotNull(nullMx.Error);
    }
}

/// <summary>The outgoing delivery worker against a scripted SMTP server standing in for the provider or the recipient's MX.</summary>
public class OutboundWorkerTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private SmtpSink _sink = null!;
    private readonly FakeMx _mx = new();

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _sink = SmtpSink.Start();
    }

    public async Task DisposeAsync()
    {
        await _sink.DisposeAsync();
        await _host.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Through a provider account
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_queued_message_is_sent_through_its_provider_account()
    {
        long account = await AddAccountAsync();
        long id = await EnqueueAsync(account, "alice@example.test", "friend@outside.test", "other@outside.test");

        Assert.Equal(1, await Worker().ProcessDueAsync());

        SinkMessage sent = Assert.Single(_sink.Messages);
        Assert.Equal("alice@example.test", sent.MailFrom);
        Assert.Equal(new[] { "friend@outside.test", "other@outside.test" }, sent.Recipients.OrderBy(r => r));
        Assert.Equal("relay-user", sent.AuthUser);
        Assert.Contains("Subject: Queued", sent.Data);
        Assert.DoesNotContain("Return-Path", sent.Data);

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Sent, row.Status);
        Assert.NotNull(row.SentDate);
        Assert.Null(row.LastError);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains(await ActivityAsync(), log => log.Category == ActivityCategory.Queue && log.Level == ActivityLevel.Info && log.Message.StartsWith("Sent 'Queued'"));

        // Nothing is due any more.
        Assert.Equal(0, await Worker().ProcessDueAsync());
        Assert.Single(_sink.Messages);
    }

    [DbFact]
    public async Task A_temporary_failure_is_retried_along_the_schedule()
    {
        _host.Config.Queue.RetryMinutes = new[] { 5, 15 };
        _sink.RecipientReply = _ => "451 4.3.0 Try again later";
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "friend@outside.test");
        OutboundWorker worker = Worker();

        await worker.ProcessDueAsync();
        OutboundMessage first = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, first.Status);
        Assert.Equal(1, first.AttemptCount);
        AssertNear(DateTime.UtcNow.AddMinutes(5), first.NextAttemptDate);
        Assert.Contains("451 4.3.0 Try again later", first.LastError);
        Assert.Empty(_sink.Messages);
        Assert.Equal(0, await worker.ProcessDueAsync());

        await MakeDueAsync(id);
        await worker.ProcessDueAsync();
        AssertNear(DateTime.UtcNow.AddMinutes(15), (await LoadAsync(id)).NextAttemptDate);

        // Beyond the list the last interval repeats.
        await MakeDueAsync(id);
        await worker.ProcessDueAsync();
        OutboundMessage third = await LoadAsync(id);
        Assert.Equal(3, third.AttemptCount);
        AssertNear(DateTime.UtcNow.AddMinutes(15), third.NextAttemptDate);

        _sink.RecipientReply = _ => null;
        await MakeDueAsync(id);
        await worker.ProcessDueAsync();
        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(id)).Status);
        Assert.Single(_sink.Messages);
        Assert.Contains(await ActivityAsync(), log => log.Level == ActivityLevel.Warning && log.Message.Contains("deferred (attempt 1)"));
        Assert.Empty(await InboxAsync(_seed.AliceMailbox.Id));
    }

    [DbFact]
    public async Task An_unreachable_provider_is_a_temporary_failure()
    {
        long id = await EnqueueAsync(await AddAccountAsync(port: ClosedPort()), "alice@example.test", "friend@outside.test");

        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, row.Status);
        Assert.Contains("cannot connect", row.LastError);
        Assert.Empty(await InboxAsync(_seed.AliceMailbox.Id));
    }

    [DbFact]
    public async Task A_permanently_refused_recipient_bounces_into_the_senders_mailbox()
    {
        _sink.RecipientReply = _ => "550 5.1.1 No such user here";
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "nobody@outside.test");

        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Failed, row.Status);
        Assert.Equal(new[] { "nobody@outside.test" }, row.Recipients);
        Assert.Contains("550 5.1.1 No such user here", row.LastError);

        MailMessage bounce = Assert.Single(await InboxAsync(_seed.AliceMailbox.Id));
        Assert.Equal("Undelivered Mail Returned to Sender", bounce.Subject);
        Assert.Equal("postmaster@mail.example.test", bounce.FromAddress);
        string text = TextOf(bounce);
        Assert.Contains("<nobody@outside.test>: 127.0.0.1: 550 5.1.1 No such user here", text);
        Assert.Contains("Subject: Queued", text);
        Assert.Contains(await ActivityAsync(), log => log.Level == ActivityLevel.Error && log.Message.Contains("A bounce was delivered to the sender"));
    }

    [DbFact]
    public async Task Partial_failures_never_send_twice_to_an_accepted_recipient()
    {
        _sink.RecipientReply = address => address switch
        {
            "gone@outside.test" => "550 5.1.1 Unknown user",
            "busy@outside.test" => "452 4.2.2 Mailbox full",
            _ => null,
        };
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "ok@outside.test", "gone@outside.test", "busy@outside.test");
        OutboundWorker worker = Worker();

        await worker.ProcessDueAsync();

        SinkMessage first = Assert.Single(_sink.Messages);
        Assert.Equal(new[] { "ok@outside.test" }, first.Recipients);
        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, row.Status);
        Assert.Equal(new[] { "busy@outside.test" }, row.Recipients);
        string bounce = TextOf(Assert.Single(await InboxAsync(_seed.AliceMailbox.Id)));
        Assert.Contains("<gone@outside.test>: 127.0.0.1: 550 5.1.1 Unknown user", bounce);
        Assert.DoesNotContain("<busy@outside.test>:", bounce);
        Assert.DoesNotContain("<ok@outside.test>:", bounce);

        _sink.RecipientReply = _ => null;
        await MakeDueAsync(id);
        await worker.ProcessDueAsync();

        Assert.Equal(2, _sink.Messages.Count);
        Assert.Equal(new[] { "busy@outside.test" }, _sink.Messages[1].Recipients);
        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(id)).Status);
        Assert.Single(await InboxAsync(_seed.AliceMailbox.Id));
    }

    [DbFact]
    public async Task A_message_refused_after_the_data_bounces_for_every_recipient()
    {
        _sink.DataReply = () => "554 5.7.1 Message rejected as spam";
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "friend@outside.test", "other@outside.test");

        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Failed, row.Status);
        Assert.Equal(new[] { "friend@outside.test", "other@outside.test" }, row.Recipients.OrderBy(r => r));
        string bounce = TextOf(Assert.Single(await InboxAsync(_seed.AliceMailbox.Id)));
        Assert.Contains("<friend@outside.test>: 127.0.0.1: 554 5.7.1 Message rejected as spam", bounce);
        Assert.Contains("<other@outside.test>: 127.0.0.1: 554 5.7.1 Message rejected as spam", bounce);
    }

    [DbFact]
    public async Task A_disabled_account_holds_its_messages_back()
    {
        long account = await AddAccountAsync();
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.MailAccounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsEnabled, false));
        }

        long id = await EnqueueAsync(account, "alice@example.test", "friend@outside.test");
        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, row.Status);
        Assert.Contains("is disabled", row.LastError);
        Assert.Empty(_sink.Messages);
    }

    [DbFact]
    public async Task A_refused_envelope_sender_is_tried_again_with_the_accounts_own_address()
    {
        long account = await AddAccountAsync(address: "relay@provider.test");
        _sink.MailFromReply = address => address == "relay@provider.test" ? null : "553 5.7.1 Sender address rejected: not owned by user";
        long id = await EnqueueAsync(account, "alice@example.test", "friend@outside.test");

        await Worker().ProcessDueAsync();

        SinkMessage sent = Assert.Single(_sink.Messages);
        Assert.Equal("relay@provider.test", sent.MailFrom);
        Assert.Contains("From: Alice <alice@example.test>", sent.Data);
        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(id)).Status);
    }

    [DbFact]
    public async Task Messages_that_stay_undeliverable_too_long_fail_with_a_bounce()
    {
        _sink.RecipientReply = _ => "451 4.3.0 Try again later";
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "friend@outside.test");
        await UpdateAsync(id, s => s.SetProperty(o => o.CreateDate, DateTime.UtcNow.AddHours(-73)));

        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Failed, row.Status);
        Assert.StartsWith("Given up after 72 hours.", row.LastError);
        MailMessage bounce = Assert.Single(await InboxAsync(_seed.AliceMailbox.Id));
        Assert.Contains("Delivery was tried for 72 hours", TextOf(bounce));
    }

    [DbFact]
    public async Task External_and_null_senders_get_no_bounce()
    {
        _sink.RecipientReply = _ => "550 5.1.1 Unknown user";
        long account = await AddAccountAsync();
        long external = await EnqueueAsync(account, "someone@outside.test", "nobody@outside.test");
        long nullSender = await EnqueueAsync(account, string.Empty, "nobody@outside.test");

        await Worker().ProcessDueAsync();

        Assert.Equal(OutboundStatus.Failed, (await LoadAsync(external)).Status);
        Assert.Equal(OutboundStatus.Failed, (await LoadAsync(nullSender)).Status);
        using IServiceScope scope = _host.Scope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.CountAsync());
    }

    [DbFact]
    public async Task Two_workers_never_send_the_same_message()
    {
        long account = await AddAccountAsync();
        var ids = new List<long>();
        for (int i = 0; i < 30; i++)
        {
            ids.Add(await EnqueueAsync(account, "alice@example.test", $"friend{i}@outside.test"));
        }

        int[] processed = await Task.WhenAll(Worker().ProcessDueAsync(), Worker().ProcessDueAsync());

        Assert.Equal(30, processed.Sum());
        Assert.Equal(30, _sink.Messages.Count);
        Assert.Equal(30, _sink.Messages.Select(m => m.Recipients.Single()).Distinct().Count());
        foreach (long id in ids)
        {
            OutboundMessage row = await LoadAsync(id);
            Assert.Equal(OutboundStatus.Sent, row.Status);
            Assert.Equal(1, row.AttemptCount);
        }
    }

    [DbFact]
    public async Task A_message_left_in_sending_is_taken_up_again_once_its_lease_is_over()
    {
        long account = await AddAccountAsync();
        long stale = await EnqueueAsync(account, "alice@example.test", "stale@outside.test");
        long running = await EnqueueAsync(account, "alice@example.test", "running@outside.test");
        await UpdateAsync(stale, s => s.SetProperty(o => o.Status, OutboundStatus.Sending).SetProperty(o => o.NextAttemptDate, DateTime.UtcNow.AddMinutes(-1)));
        await UpdateAsync(running, s => s.SetProperty(o => o.Status, OutboundStatus.Sending).SetProperty(o => o.NextAttemptDate, DateTime.UtcNow.AddMinutes(30)));

        Assert.Equal(1, await Worker().ProcessDueAsync());

        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(stale)).Status);
        Assert.Equal(OutboundStatus.Sending, (await LoadAsync(running)).Status);
        Assert.Equal("stale@outside.test", Assert.Single(_sink.Messages).Recipients.Single());
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Direct delivery
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Direct_delivery_goes_to_the_mail_servers_of_each_recipient_domain()
    {
        long id = await EnqueueAsync(null, "alice@example.test", "friend@outside.test", "other@elsewhere.test");

        await Worker().ProcessDueAsync();

        Assert.Equal(2, _sink.Messages.Count);
        Assert.All(_sink.Helos, helo => Assert.Equal("mail.example.test", helo));
        Assert.Equal(new[] { "elsewhere.test", "outside.test" }, _mx.Asked.OrderBy(d => d));
        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(id)).Status);
    }

    [DbFact]
    public async Task Direct_delivery_to_a_domain_that_takes_no_mail_bounces_at_once()
    {
        _mx.Answer = domain => new MxLookup(Array.Empty<string>(), $"The domain {domain} does not exist.", IsPermanent: true);
        long id = await EnqueueAsync(null, "alice@example.test", "friend@nowhere.test");

        await Worker().ProcessDueAsync();

        Assert.Equal(OutboundStatus.Failed, (await LoadAsync(id)).Status);
        Assert.Contains("The domain nowhere.test does not exist.", TextOf(Assert.Single(await InboxAsync(_seed.AliceMailbox.Id))));
    }

    [DbFact]
    public async Task A_failing_domain_does_not_cost_the_outcome_of_the_others()
    {
        _mx.Answer = domain => domain == "broken.test" ? throw new InvalidOperationException("resolver exploded") : new MxLookup(new[] { "127.0.0.1" });
        long id = await EnqueueAsync(null, "alice@example.test", "friend@outside.test", "x@broken.test");
        OutboundWorker worker = Worker();

        await worker.ProcessDueAsync();

        Assert.Equal(new[] { "friend@outside.test" }, Assert.Single(_sink.Messages).Recipients);
        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, row.Status);
        Assert.Equal(new[] { "x@broken.test" }, row.Recipients);
        Assert.Contains("resolver exploded", row.LastError);

        // The next attempt goes to the broken domain only: friend@ never gets a second copy.
        _mx.Answer = _ => new MxLookup(new[] { "127.0.0.1" });
        await MakeDueAsync(id);
        await worker.ProcessDueAsync();
        Assert.Equal(2, _sink.Messages.Count);
        Assert.Equal(new[] { "x@broken.test" }, _sink.Messages[1].Recipients);
        Assert.Equal(OutboundStatus.Sent, (await LoadAsync(id)).Status);
    }

    [DbFact]
    public async Task An_attempt_that_takes_too_long_is_cut_off_and_tried_again_later()
    {
        var hold = new TaskCompletionSource();
        _sink.BeforeDataReply = () => hold.Task;
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "friend@outside.test");
        try
        {
            await Worker(maxAttempt: TimeSpan.FromSeconds(2)).ProcessDueAsync();

            OutboundMessage row = await LoadAsync(id);
            Assert.Equal(OutboundStatus.Pending, row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.Contains("took longer than", row.LastError);
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [DbFact]
    public async Task A_slow_receiving_server_does_not_hold_up_other_mail()
    {
        var hold = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        await using SmtpSink slow = SmtpSink.Start();
        slow.BeforeDataReply = () =>
        {
            holding.TrySetResult();
            return hold.Task;
        };
        long slowAccount = await AddAccountAsync(address: "slow@example.test", port: slow.Port);
        long fastAccount = await AddAccountAsync();
        using OutboundWorker worker = Worker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            long slowId = await EnqueueAsync(slowAccount, "alice@example.test", "slow@outside.test");
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await EnqueueAsync(fastAccount, "alice@example.test", "fast@outside.test");
            await WaitUntilAsync(() => _sink.Messages.Count == 1);
            Assert.Empty(slow.Messages);

            hold.TrySetResult();
            await WaitUntilAsync(() => slow.Messages.Count == 1);
            await WaitUntilAsync(async () => (await LoadAsync(slowId)).Status == OutboundStatus.Sent);
        }
        finally
        {
            hold.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [DbFact]
    public async Task An_outcome_is_dropped_when_another_worker_took_the_entry_over()
    {
        var hold = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        _sink.BeforeDataReply = () =>
        {
            holding.TrySetResult();
            return hold.Task;
        };
        long id = await EnqueueAsync(await AddAccountAsync(), "alice@example.test", "friend@outside.test");

        Task processing = Worker().ProcessDueAsync();
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Meanwhile the lease ran out and another worker claimed the entry with a lease of its own.
        await UpdateAsync(id, s => s.SetProperty(o => o.NextAttemptDate, DateTime.UtcNow.AddMinutes(50)));
        hold.TrySetResult();
        await processing;

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Sending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.SentDate);
    }

    [DbFact]
    public async Task Without_an_account_and_without_direct_delivery_the_message_waits()
    {
        _host.Config.Queue.AllowDirectDelivery = false;
        long id = await EnqueueAsync(null, "alice@example.test", "friend@outside.test");

        await Worker().ProcessDueAsync();

        OutboundMessage row = await LoadAsync(id);
        Assert.Equal(OutboundStatus.Pending, row.Status);
        Assert.Contains("direct delivery is switched off", row.LastError);
        Assert.Empty(_sink.Messages);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Housekeeping, the running service, end to end
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Housekeeping_removes_old_entries_only()
    {
        _host.Config.Retention.SentQueueDays = 14;
        DateTime now = DateTime.UtcNow;
        long sentOld = await AddRowAsync(OutboundStatus.Sent, now.AddDays(-20));
        long sentNew = await AddRowAsync(OutboundStatus.Sent, now.AddDays(-2));
        long cancelledOld = await AddRowAsync(OutboundStatus.Cancelled, now.AddDays(-20));
        long failedOld = await AddRowAsync(OutboundStatus.Failed, now.AddDays(-31));
        long failedNew = await AddRowAsync(OutboundStatus.Failed, now.AddDays(-10));
        long pendingOld = await AddRowAsync(OutboundStatus.Pending, now.AddDays(-40));

        Assert.Equal(3, await Worker().CleanUpAsync());

        using IServiceScope scope = _host.Scope();
        List<long> left = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.Select(o => o.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { sentNew, failedNew, pendingOld }.OrderBy(i => i), left);
        Assert.DoesNotContain(sentOld, left);
        Assert.DoesNotContain(cancelledOld, left);
        Assert.DoesNotContain(failedOld, left);
    }

    [DbFact]
    public async Task The_running_worker_wakes_up_when_something_is_queued()
    {
        long account = await AddAccountAsync();
        using OutboundWorker worker = Worker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EnqueueAsync(account, "alice@example.test", "friend@outside.test");

            // The poll interval is 30 seconds; the signal makes it immediate.
            await WaitUntilAsync(() => _sink.Messages.Count == 1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [DbFact]
    public async Task A_message_submitted_over_smtp_is_delivered_by_the_worker()
    {
        await AddAccountAsync();
        await using (RunningSmtpServer server = await RunningSmtpServer.StartAsync(_host))
        using (SmtpClient client = await TestMailClients.SignInAsync(server.SubmissionPort, "alice"))
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Alice", "alice@example.test"));
            message.To.Add(new MailboxAddress("Friend", "friend@outside.test"));
            message.Subject = "End to end";
            message.Body = new TextPart("plain") { Text = "Hello from MatMail" };
            await client.SendAsync(message);
        }

        Assert.Equal(1, await Worker().ProcessDueAsync());

        SinkMessage sent = Assert.Single(_sink.Messages);
        Assert.Equal("alice@example.test", sent.MailFrom);
        Assert.Equal(new[] { "friend@outside.test" }, sent.Recipients);
        Assert.Contains("Received: from client.test ([127.0.0.1])", sent.Data);
        Assert.Contains("Hello from MatMail", sent.Data);
        Assert.DoesNotContain("Return-Path:", sent.Data);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------------

    private OutboundWorker Worker(TimeSpan? maxAttempt = null) => new(
        _host.Services.GetRequiredService<IServiceScopeFactory>(),
        _host.Config,
        _host.Services.GetRequiredService<OutboundSignal>(),
        _mx,
        _host.Services.GetRequiredService<ILogger<OutboundWorker>>(),
        new OutboundWorkerOptions { DirectPort = _sink.Port, DirectTimeout = TimeSpan.FromSeconds(10), MaxAttemptDuration = maxAttempt ?? TimeSpan.FromMinutes(15) });

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time.");
            await Task.Delay(25);
        }
    }

    private async Task<long> AddAccountAsync(string address = "alice@example.test", int? port = null)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount
        {
            TenantId = _seed.Tenant.Id,
            Name = "Provider",
            Address = address,
            ReceiveProtocol = ReceiveProtocol.None,
            ReceiveUsername = "relay-user",
            ReceivePasswordProtected = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("relay-password"),
            SendHost = "127.0.0.1",
            SendPort = port ?? _sink.Port,
            SendSecurity = ConnectionSecurity.None,
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<long> EnqueueAsync(long? accountId, string from, params string[] recipients)
    {
        using IServiceScope scope = _host.Scope();
        byte[] raw = System.Text.Encoding.UTF8.GetBytes("Return-Path: <" + from + ">\r\n" +
            System.Text.Encoding.UTF8.GetString(RawMail.Build($"Alice <{(from.Length == 0 ? "alice@example.test" : from)}>", string.Join(", ", recipients), "Queued", "Hello")));
        OutboundMessage message = await scope.ServiceProvider.GetRequiredService<OutboundQueue>()
            .EnqueueAsync(_seed.Tenant.Id, accountId, from, recipients, raw, "Queued", _seed.AliceMailbox.Id, _seed.Alice.Id);
        return message.Id;
    }

    private async Task<long> AddRowAsync(OutboundStatus status, DateTime date)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var row = new OutboundMessage
        {
            TenantId = _seed.Tenant.Id, EnvelopeFrom = "alice@example.test", Recipients = new[] { "x@outside.test" }, Raw = new byte[] { 1 },
            Status = status, NextAttemptDate = date.AddDays(365), SentDate = status == OutboundStatus.Sent ? date : null,
        };
        db.OutboundMessages.Add(row);
        await db.SaveChangesAsync();
        await UpdateAsync(row.Id, s => s.SetProperty(o => o.CreateDate, date).SetProperty(o => o.UpdateDate, date));
        return row.Id;
    }

    private async Task UpdateAsync(long id, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<OutboundMessage>> setters)
    {
        using IServiceScope scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.Where(o => o.Id == id).ExecuteUpdateAsync(setters);
    }

    private Task MakeDueAsync(long id) => UpdateAsync(id, s => s.SetProperty(o => o.NextAttemptDate, DateTime.UtcNow.AddSeconds(-1)));

    private async Task<OutboundMessage> LoadAsync(long id)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private async Task<List<MailMessage>> InboxAsync(long mailboxId)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.AsNoTracking().Include(m => m.Content)
            .Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == FolderKind.Inbox)
            .OrderBy(m => m.Uid)
            .ToListAsync();
    }

    private async Task<List<ActivityLog>> ActivityAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().ActivityLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
    }

    private static string TextOf(MailMessage message)
    {
        using var stream = new MemoryStream(message.Content!.Raw!);
        return MimeMessage.Load(stream).TextBody ?? string.Empty;
    }

    private static void AssertNear(DateTime expected, DateTime actual)
        => Assert.InRange(actual.ToUniversalTime(), expected.AddMinutes(-1), expected.AddMinutes(1));

    /// <summary>A port nothing listens on.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Every domain's mail server is the local sink (or what the test says).</summary>
    private sealed class FakeMx : IMxResolver
    {
        private readonly List<string> _asked = new();

        public Func<string, MxLookup> Answer { get; set; } = _ => new MxLookup(new[] { "127.0.0.1" });

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return _asked.ToList();
                }
            }
        }

        public Task<MxLookup> ResolveAsync(string domain, CancellationToken cancel)
        {
            lock (_asked)
            {
                _asked.Add(domain);
            }

            return Task.FromResult(Answer(domain));
        }
    }
}

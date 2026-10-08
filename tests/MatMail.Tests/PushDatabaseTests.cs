using System.Security.Cryptography;
using System.Text.Json;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Push;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatMail.Tests;

/// <summary>Takes the place of the push services: remembers what was sent and answers as told.</summary>
public sealed class FakePushSender : IPushSender
{
    private readonly object _lock = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PushOutcome Answer { get; set; } = PushOutcome.Delivered;
    public List<(string Endpoint, JsonElement Message, PushOptions Options)> Sent { get; } = [];

    /// <summary>Completes when the first message was sent.</summary>
    public Task FirstSent => _first.Task;

    public Task<PushOutcome> SendAsync(PushSubscription subscription, byte[] message, PushOptions options, CancellationToken cancel)
    {
        lock (_lock)
        {
            Sent.Add((subscription.Endpoint, JsonDocument.Parse(message).RootElement.Clone(), options));
        }

        _first.TrySetResult();
        return Task.FromResult(Answer);
    }
}

/// <summary>Registering devices, deciding who is told about which mail, and keeping the list of devices clean.</summary>
public class PushDatabaseTests : IAsyncLifetime
{
    private readonly FakePushSender _sender = new();
    private readonly string _keyDir = Path.Combine(Path.GetTempPath(), "matmail-pushdb-" + Guid.NewGuid().ToString("N"));
    private TestHost _host = null!;
    private Seed _seed = null!;
    private long _aliceInbox;
    private long _aliceSent;
    private long _infoInbox;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(_sender);
            services.RemoveAll<PushKeys>();
            services.AddSingleton(sp => new PushKeys(sp.GetRequiredService<SecretProtector>(), _keyDir));
        });
        _seed = await _host.SeedAsync();

        using IServiceScope scope = _host.Scope();
        var folders = scope.ServiceProvider.GetRequiredService<FolderService>();
        _aliceInbox = (await folders.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox))!.Id;
        _aliceSent = (await folders.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Sent))!.Id;
        _infoInbox = (await folders.FindByKindAsync(_seed.Info.Id, FolderKind.Inbox))!.Id;

        // Bob may read the shared mailbox; Alice may not.
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailboxPermissions.Add(new MailboxPermission { TenantId = _seed.Tenant.Id, MailboxId = _seed.Info.Id, UserId = _seed.Bob.Id, Access = MailboxAccess.Read });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try
        {
            Directory.Delete(_keyDir, recursive: true);
        }
        catch (IOException)
        {
            // the temp folder is cleaned up by the system anyway
        }
    }

    private static (string P256dh, string Auth) NewKeys()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (WebPushEncryption.ToBase64Url(WebPushEncryption.UncompressedPoint(key)), WebPushEncryption.ToBase64Url(RandomNumberGenerator.GetBytes(16)));
    }

    private async Task SubscribeAsync(User user, string endpoint, bool ownMailboxOnly = true)
    {
        using IServiceScope scope = _host.Scope();
        (string key, string auth) = NewKeys();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<PushService>().SubscribeAsync(user.Id, endpoint, key, auth, "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Safari/537.36", ownMailboxOnly, null, default));
    }

    private async Task<MailMessage> ArriveAsync(long folderId, string from, string subject, bool read = false)
    {
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        return await store.AddAsync(folderId, new NewMessage(RawMail.Build(from, "someone@example.test", subject, "text")) { IsRead = read });
    }

    private async Task<IReadOnlyList<PlannedPush>> PlanAsync(params MailMessage[] messages)
    {
        using IServiceScope scope = _host.Scope();
        var planner = scope.ServiceProvider.GetRequiredService<PushPlanner>();
        return await planner.PlanAsync(messages.Select(m => new MailEvent(MailEventKind.NewMessage, m.TenantId, m.MailboxId, m.FolderId, m.Id)).ToList(), default);
    }

    private static JsonElement Payload(PlannedPush plan) => JsonDocument.Parse(plan.Message).RootElement;

    // ---- the devices ------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_device_needs_a_secure_address_and_real_keys()
    {
        using IServiceScope scope = _host.Scope();
        var push = scope.ServiceProvider.GetRequiredService<PushService>();
        (string key, string auth) = NewKeys();
        const string refused = "This device cannot receive notifications.";

        Assert.Equal(refused, await push.SubscribeAsync(_seed.Alice.Id, "http://push.example.test/a", key, auth, null, null, null, default));          // not https
        Assert.Equal(refused, await push.SubscribeAsync(_seed.Alice.Id, "", key, auth, null, null, null, default));
        Assert.Equal(refused, await push.SubscribeAsync(_seed.Alice.Id, "https://push.example.test/" + new string('a', 1000), key, auth, null, null, null, default));
        Assert.Equal(refused, await push.SubscribeAsync(_seed.Alice.Id, "https://push.example.test/a", "AAAA", auth, null, null, null, default));       // not a key
        Assert.Equal(refused, await push.SubscribeAsync(_seed.Alice.Id, "https://push.example.test/a", key, "AAAA", null, null, null, default));        // not an auth secret
        Assert.Empty(await push.ListAsync(_seed.Alice.Id, default));

        Assert.Null(await push.SubscribeAsync(_seed.Alice.Id, "https://push.example.test/a", key, auth, "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Safari/537.36", null, null, default));
        PushSubscription device = Assert.Single(await push.ListAsync(_seed.Alice.Id, default));
        Assert.Equal("Chrome / Windows", device.DeviceName);
        Assert.True(device.OwnMailboxOnly);   // the default: only the own mailbox
    }

    [DbFact]
    public async Task A_browser_belongs_to_one_person_at_a_time_and_a_replaced_subscription_hands_over_the_choice()
    {
        using IServiceScope scope = _host.Scope();
        var push = scope.ServiceProvider.GetRequiredService<PushService>();
        (string key, string auth) = NewKeys();

        Assert.Null(await push.SubscribeAsync(_seed.Alice.Id, "https://push.example.test/shared-browser", key, auth, null, true, null, default));
        // Another person signs in in the same browser: the address is the same, the device is now theirs.
        Assert.Null(await push.SubscribeAsync(_seed.Bob.Id, "https://push.example.test/shared-browser", key, auth, null, false, null, default));
        Assert.Empty(await push.ListAsync(_seed.Alice.Id, default));
        Assert.False(Assert.Single(await push.ListAsync(_seed.Bob.Id, default)).OwnMailboxOnly);

        // The browser replaces its subscription: the new one carries the choice of the old, which goes.
        Assert.Null(await push.SubscribeAsync(_seed.Bob.Id, "https://push.example.test/renewed", key, auth, null, null, "https://push.example.test/shared-browser", default));
        PushSubscription renewed = Assert.Single(await push.ListAsync(_seed.Bob.Id, default));
        Assert.Equal("https://push.example.test/renewed", renewed.Endpoint);
        Assert.False(renewed.OwnMailboxOnly);

        Assert.False(await push.UnsubscribeAsync(_seed.Alice.Id, renewed.Endpoint, default));   // not hers
        Assert.NotNull(await push.FindAsync(_seed.Bob.Id, renewed.Endpoint, default));
        Assert.True(await push.UnsubscribeAsync(_seed.Bob.Id, renewed.Endpoint, default));
        Assert.Null(await push.FindAsync(_seed.Bob.Id, renewed.Endpoint, default));
    }

    [DbFact]
    public async Task Only_the_newest_devices_of_a_person_are_kept()
    {
        using IServiceScope scope = _host.Scope();
        var push = scope.ServiceProvider.GetRequiredService<PushService>();
        (string key, string auth) = NewKeys();
        for (int i = 0; i < PushService.MaxDevicesPerUser + 3; i++)
        {
            Assert.Null(await push.SubscribeAsync(_seed.Alice.Id, $"https://push.example.test/device-{i}", key, auth, null, null, null, default));
        }

        List<PushSubscription> devices = (await push.ListAsync(_seed.Alice.Id, default)).ToList();
        Assert.Equal(PushService.MaxDevicesPerUser, devices.Count);
        Assert.Contains(devices, d => d.Endpoint.EndsWith("device-" + (PushService.MaxDevicesPerUser + 2)));
        Assert.DoesNotContain(devices, d => d.Endpoint.EndsWith("device-0"));
    }

    [DbFact]
    public async Task What_the_push_services_answer_keeps_the_list_of_devices_clean()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/ok");
        await SubscribeAsync(_seed.Alice, "https://push.example.test/gone");
        await SubscribeAsync(_seed.Alice, "https://push.example.test/failing");

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Dictionary<string, long> ids = await db.PushSubscriptions.ToDictionaryAsync(s => s.Endpoint[(s.Endpoint.LastIndexOf('/') + 1)..], s => s.Id);

        await PushService.RecordOutcomesAsync(db, new Dictionary<long, PushOutcome> { [ids["ok"]] = PushOutcome.Delivered, [ids["gone"]] = PushOutcome.Gone, [ids["failing"]] = PushOutcome.Failed }, 3, default);
        Assert.Equal(new[] { "failing", "ok" }, (await db.PushSubscriptions.AsNoTracking().ToListAsync()).Select(s => s.Endpoint[(s.Endpoint.LastIndexOf('/') + 1)..]).Order().ToArray());
        PushSubscription ok = await db.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Id == ids["ok"]);
        Assert.NotNull(ok.LastSuccessDate);

        // A device that fails again and again goes after a while; one success sets the count back.
        for (int i = 0; i < 2; i++)
        {
            await PushService.RecordOutcomesAsync(db, new Dictionary<long, PushOutcome> { [ids["failing"]] = PushOutcome.Failed }, 3, default);
        }

        Assert.False(await db.PushSubscriptions.AnyAsync(s => s.Id == ids["failing"]));
    }

    // ---- who is told about what ---------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_owner_is_told_who_wrote_and_what_about_and_the_notification_opens_that_message()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        await SubscribeAsync(_seed.Bob, "https://push.example.test/bob");   // Bob owns another mailbox
        MailMessage message = await ArriveAsync(_aliceInbox, "Anna Berger <anna@partner.test>", "Offer for hosting");

        PlannedPush plan = Assert.Single(await PlanAsync(message));

        Assert.Equal("https://push.example.test/alice", plan.Subscription.Endpoint);
        JsonElement payload = Payload(plan);
        Assert.Equal("Anna Berger", payload.GetProperty("title").GetString());
        Assert.Equal("Offer for hosting", payload.GetProperty("body").GetString());
        Assert.Equal($"/Mail#mailbox={_seed.AliceMailbox.Id}&folder={_aliceInbox}&m={message.Id}", payload.GetProperty("url").GetString());
        Assert.Equal("mail-" + message.Id, payload.GetProperty("tag").GetString());
        Assert.Equal("m" + _seed.AliceMailbox.Id, plan.Topic);
    }

    [DbFact]
    public async Task Only_unread_mail_in_an_inbox_counts()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");

        Assert.Empty(await PlanAsync(await ArriveAsync(_aliceInbox, "a@x.test", "already read", read: true)));
        Assert.Empty(await PlanAsync(await ArriveAsync(_aliceSent, "a@x.test", "in the sent folder")));
        Assert.Empty(await PlanAsync());
        Assert.Single(await PlanAsync(await ArriveAsync(_aliceInbox, "a@x.test", "new")));
    }

    [DbFact]
    public async Task Old_mail_is_no_news_even_when_it_arrives_now()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        using IServiceScope scope = _host.Scope();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        // The history of a provider account that is synchronised for the first time comes through the same door as a new message.
        MailMessage old = await store.AddAsync(_aliceInbox, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "last year", "text")) { ReceivedDate = DateTime.UtcNow.AddDays(-400) });
        MailMessage lateButToday = await store.AddAsync(_aliceInbox, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "an hour ago", "text")) { ReceivedDate = DateTime.UtcNow.AddHours(-1) });

        Assert.Empty(await PlanAsync(old));
        Assert.Single(await PlanAsync(lateButToday));
        Assert.Equal("an hour ago", Payload(Assert.Single(await PlanAsync(old, lateButToday))).GetProperty("body").GetString());   // the old one does not count in a group either
    }

    [DbFact]
    public async Task A_shared_mailbox_is_told_to_those_who_asked_for_it_and_may_read_it()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice", ownMailboxOnly: false);   // wants everything, but may not read info@
        await SubscribeAsync(_seed.Bob, "https://push.example.test/bob-all", ownMailboxOnly: false);
        MailMessage message = await ArriveAsync(_infoInbox, "Customer <c@client.test>", "Question");

        PlannedPush plan = Assert.Single(await PlanAsync(message));
        Assert.Equal("https://push.example.test/bob-all", plan.Subscription.Endpoint);

        // The same device, asking for the own mailbox only: nothing.
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().PushSubscriptions.Where(s => s.Endpoint.EndsWith("bob-all")).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnMailboxOnly, true));
        }

        Assert.Empty(await PlanAsync(message));
    }

    [DbFact]
    public async Task Several_messages_at_once_make_one_notification_and_a_person_who_is_off_gets_none()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        MailMessage first = await ArriveAsync(_aliceInbox, "One <one@x.test>", "First");
        MailMessage second = await ArriveAsync(_aliceInbox, "Two <two@x.test>", "Second");
        MailMessage third = await ArriveAsync(_aliceInbox, "Three <three@x.test>", "Third");

        PlannedPush plan = Assert.Single(await PlanAsync(first, second, third));
        JsonElement payload = Payload(plan);
        Assert.Equal("3 new messages", payload.GetProperty("title").GetString());
        Assert.Equal("Three: Third", payload.GetProperty("body").GetString());   // the newest one
        Assert.Equal($"/Mail#mailbox={_seed.AliceMailbox.Id}&folder={_aliceInbox}", payload.GetProperty("url").GetString());

        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Users.IgnoreQueryFilters().Where(u => u.Id == _seed.Alice.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        }

        Assert.Empty(await PlanAsync(first, second, third));
    }

    [DbFact]
    public async Task Long_texts_are_cut_so_that_they_always_fit_a_push_message()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        MailMessage message = await ArriveAsync(_aliceInbox, "\"" + new string('N', 300) + "\" <n@x.test>", new string('S', 1000));

        JsonElement payload = Payload(Assert.Single(await PlanAsync(message)));

        Assert.True(payload.GetProperty("title").GetString()!.Length <= 80);
        Assert.True(payload.GetProperty("body").GetString()!.Length <= 160);
        Assert.EndsWith("…", payload.GetProperty("body").GetString());
    }

    // ---- sending ---------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_notifier_sends_and_notes_what_the_push_service_answered()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        await SubscribeAsync(_seed.Alice, "https://push.example.test/old-phone");
        MailMessage message = await ArriveAsync(_aliceInbox, "Anna <anna@partner.test>", "Hello");
        var notifier = new PushNotifier(_host.Services.GetRequiredService<MailEventHub>(), _host.Services.GetRequiredService<IServiceScopeFactory>(), _host.Config, NullLogger<PushNotifier>.Instance);

        // One device is gone (the user removed the app), the other is fine.
        _sender.Answer = PushOutcome.Delivered;
        await notifier.NotifyAsync([new MailEvent(MailEventKind.NewMessage, message.TenantId, message.MailboxId, message.FolderId, message.Id)], default);

        Assert.Equal(2, _sender.Sent.Count);
        Assert.All(_sender.Sent, s => Assert.Equal("Anna", s.Message.GetProperty("title").GetString()));
        Assert.Equal("m" + _seed.AliceMailbox.Id, _sender.Sent[0].Options.Topic);
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Assert.All(await db.PushSubscriptions.AsNoTracking().ToListAsync(), s => Assert.NotNull(s.LastSuccessDate));
        }

        _sender.Answer = PushOutcome.Gone;
        await notifier.NotifyAsync([new MailEvent(MailEventKind.NewMessage, message.TenantId, message.MailboxId, message.FolderId, message.Id)], default);
        using (IServiceScope scope = _host.Scope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().PushSubscriptions.AsNoTracking().ToListAsync());
        }
    }

    [DbFact]
    public async Task What_arrives_in_the_mail_store_reaches_the_devices_through_the_hub()
    {
        await SubscribeAsync(_seed.Alice, "https://push.example.test/alice");
        var notifier = new PushNotifier(
            _host.Services.GetRequiredService<MailEventHub>(), _host.Services.GetRequiredService<IServiceScopeFactory>(), _host.Config, NullLogger<PushNotifier>.Instance, collect: TimeSpan.FromMilliseconds(50));
        await notifier.StartAsync(default);
        try
        {
            await ArriveAsync(_aliceInbox, "Anna <anna@partner.test>", "Through the store");   // the store publishes the event itself

            Assert.Same(_sender.FirstSent, await Task.WhenAny(_sender.FirstSent, Task.Delay(TimeSpan.FromSeconds(20))));
            Assert.Equal("Through the store", _sender.Sent[0].Message.GetProperty("body").GetString());
        }
        finally
        {
            await notifier.StopAsync(default);
        }
    }
}

using MatMail.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatMail.MailSync;

/// <summary>Tuning of the provider synchronisation. The defaults suit a small installation; the tests make them smaller.</summary>
public sealed class MailSyncOptions
{
    /// <summary>How often the scheduler looks for accounts that are due.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Messages fetched and committed together; the folder position only moves on after a whole batch is stored.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Most messages one run handles per account, so one huge mailbox does not starve the others (the rest follows at once).</summary>
    public int MaxMessagesPerRun { get; set; } = 2000;

    /// <summary>Larger messages are not downloaded; they are recorded, reported and left at the provider.</summary>
    public long MaxMessageBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Failed attempts after which a broken message is given up (recorded without a local copy and logged once).</summary>
    public int MaxMessageAttempts { get; set; } = 3;

    /// <summary>A run that showed no sign of life for this long is considered crashed; the account may be picked up again.</summary>
    public TimeSpan StaleRunAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The longest pause after failures (the back-off doubles the interval per failure up to this, or the interval if that is longer).</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>The flag comparison looks at this many of the most recent messages of a folder.</summary>
    public int FlagSyncWindow { get; set; } = 200;

    /// <summary>Live access: how long an idle provider connection is kept open for further on-demand fetches.</summary>
    public TimeSpan LiveConnectionIdle { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Live access: memory for recently fetched messages (clients often read the same message several times in a row).</summary>
    public long LiveCacheBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Live access: how long a fetched message stays in that memory.</summary>
    public TimeSpan LiveCacheLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Test hook: runs before a message is imported (account id, remote folder, remote uid); an exception counts as a failed import.</summary>
    internal Action<long, string, string>? BeforeImport { get; set; }
}

public static class MailSyncServices
{
    /// <summary>
    /// The provider synchronisation: the background scheduler (<see cref="MailSyncService"/>), the "Sync now" entry point
    /// (<see cref="MailSyncTrigger"/>) and the on-demand body fetch for live-access accounts (<see cref="IRemoteContentProvider"/>).
    /// </summary>
    public static IServiceCollection AddMailSync(this IServiceCollection services) => services.AddMailSync(_ => { });

    /// <summary>Same as <see cref="AddMailSync(IServiceCollection)"/> with adjusted <see cref="MailSyncOptions"/>.</summary>
    public static IServiceCollection AddMailSync(this IServiceCollection services, Action<MailSyncOptions> configure)
    {
        var options = new MailSyncOptions();
        configure(options);
        services.AddSingleton(options);

        services.AddSingleton<SyncFailureTracker>();
        services.AddSingleton<MailSyncRunner>();
        services.AddSingleton<MailSyncTrigger>();
        services.Replace(ServiceDescriptor.Singleton<IAccountSyncRunner, AccountSyncAdapter>());
        services.AddSingleton<RemoteContentFetcher>();
        services.AddSingleton<IRemoteContentProvider>(provider => provider.GetRequiredService<RemoteContentFetcher>());

        services.AddScoped<SyncImporter>();
        services.AddScoped<ImapAccountSync>();
        services.AddScoped<Pop3AccountSync>();

        services.AddSingleton<MailSyncService>();
        services.AddHostedService(provider => provider.GetRequiredService<MailSyncService>());
        return services;
    }
}

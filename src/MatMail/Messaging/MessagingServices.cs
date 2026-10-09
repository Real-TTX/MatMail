using MatMail.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatMail.Messaging;

public static class MessagingServices
{
    /// <summary>The mail core: store, folders, access, delivery, routing, submission and the outgoing queue.</summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<MailEventHub>();
        services.AddSingleton<OutboundSignal>();
        services.AddSingleton<AttachmentStaging>();
        services.AddSingleton<MailBodyRenderer>();

        services.AddScoped<MailStore>();
        services.AddScoped<FolderService>();
        services.AddScoped<MailboxUsageService>();
        services.AddScoped<MailAccessService>();
        services.AddScoped<MailRuleEngine>();
        services.AddScoped<MailDelivery>();
        services.AddScoped<RelayPolicy>();
        services.AddScoped<SendRouting>();
        services.AddScoped<SignatureService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<OutboundQueue>();
        services.AddScoped<MailSubmission>();
        services.AddScoped<ComposeService>();
        services.AddScoped<ContactService>();
        services.AddScoped<ProviderConnector>();
        services.TryAddSingleton<IAccountSyncRunner, UnavailableSyncRunner>();
        return services;
    }
}

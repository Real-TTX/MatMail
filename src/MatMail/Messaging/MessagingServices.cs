using MatMail.Services;

namespace MatMail.Messaging;

public static class MessagingServices
{
    /// <summary>The mail core: store, folders, access, delivery, routing, submission and the outgoing queue.</summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<MailEventHub>();
        services.AddSingleton<OutboundSignal>();

        services.AddScoped<MailStore>();
        services.AddScoped<FolderService>();
        services.AddScoped<MailAccessService>();
        services.AddScoped<MailDelivery>();
        services.AddScoped<RelayPolicy>();
        services.AddScoped<SendRouting>();
        services.AddScoped<SignatureService>();
        services.AddScoped<OutboundQueue>();
        services.AddScoped<MailSubmission>();
        services.AddScoped<ProviderConnector>();
        return services;
    }
}

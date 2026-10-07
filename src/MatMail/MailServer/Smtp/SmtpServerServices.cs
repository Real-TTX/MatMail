using MatMail.MailServer.Outbound;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatMail.MailServer.Smtp;

public static class SmtpServerServices
{
    /// <summary>
    /// The SMTP server and the outgoing delivery worker, as hosted services. Needs the mail core (<c>AddMatMailServices</c>);
    /// a <see cref="Services.CertificateProvider"/> singleton enables TLS. Both are registered as singletons too, so other parts
    /// (status pages) can ask them, e.g. for <see cref="SmtpServer.BoundPorts"/>.
    /// </summary>
    public static IServiceCollection AddSmtpServer(this IServiceCollection services)
    {
        services.TryAddSingleton<SmtpAuthThrottle>();
        services.TryAddSingleton<SmtpActivityLog>();
        services.TryAddSingleton<SmtpServer>();
        services.AddHostedService(provider => provider.GetRequiredService<SmtpServer>());

        services.TryAddSingleton<IMxResolver, DnsMxResolver>();
        services.TryAddSingleton<OutboundWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<OutboundWorker>());
        return services;
    }
}

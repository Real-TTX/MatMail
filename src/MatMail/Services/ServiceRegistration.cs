using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Push;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatMail.Services;

public static class ServiceRegistration
{
    /// <summary>
    /// Everything the application needs apart from the web pipeline: database, sign-in, tenants/users/mailboxes and the mail core.
    /// Shared by the real start-up (<c>Program.cs</c>) and by the tests, so both run the same wiring.
    /// </summary>
    public static IServiceCollection AddMatMailServices(this IServiceCollection services, AppConfig config)
    {
        services.AddSingleton(config);
        services.AddDbContext<MatMailDbContext>(options => options.UseNpgsql(config.Database.ConnectionString));

        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PasswordHasher<User>>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<TwoFactorTicket>();
        services.AddSingleton<SessionCache>();
        services.AddSingleton<SetupState>();
        services.AddSingleton<ActivityLogger>();
        services.AddSingleton<Fmt>();
        services.AddSingleton<BrandingService>();

        services.AddScoped<CurrentUser>();
        services.AddScoped<ThemeService>();
        services.AddScoped<TwoFactorPolicy>();
        services.AddScoped<AppPasswordService>();
        services.AddScoped<SignInService>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<SessionCookieEvents>();
        services.AddScoped<TenantService>();
        services.AddScoped<UserService>();
        services.AddScoped<MailboxService>();
        services.AddScoped<SetupService>();

        services.AddMessaging();
        services.AddPushNotifications();
        services.AddBackups();
        return services;
    }
}

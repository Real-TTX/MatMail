namespace MatMail.Push;

public static class PushServices
{
    /// <summary>Notifications on phones and desktops: the server's key pair, the sender, who-gets-what and the devices of the users.</summary>
    public static IServiceCollection AddPushNotifications(this IServiceCollection services)
    {
        services.AddSingleton<PushKeys>();
        services.AddHttpClient<IPushSender, WebPushClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = WebPushClient.ConnectToPublicAddressAsync,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            });
        services.AddScoped<PushPlanner>();
        services.AddScoped<PushService>();
        return services;
    }
}

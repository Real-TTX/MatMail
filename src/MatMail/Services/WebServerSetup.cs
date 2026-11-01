using MatMail.Configuration;

namespace MatMail.Services;

public static class WebServerSetup
{
    /// <summary>
    /// The web server (Kestrel): one port for the web interface, plain HTTP – a reverse proxy in front brings TLS (see
    /// <see cref="ProxyHeaders"/>); the mail servers have their own TLS. The application and the progress page of a restore at start-up
    /// both listen like this.
    /// </summary>
    public static void ConfigureMatMailWebServer(this IWebHostBuilder host, AppConfig config)
    {
        host.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = (long)Math.Max(config.Server.MaxUploadMb, 1) * 1024 * 1024 + 4 * 1024 * 1024;
            kestrel.ListenAnyIP(config.Server.WebPort);
        });
    }
}

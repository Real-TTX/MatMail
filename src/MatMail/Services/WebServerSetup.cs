using MatMail.Configuration;

namespace MatMail.Services;

public static class WebServerSetup
{
    /// <summary>
    /// The web server (Kestrel): one port for the web interface, HTTPS unless a proxy terminates TLS. The application and the progress
    /// page of a restore at start-up both listen like this.
    /// </summary>
    public static void ConfigureMatMailWebServer(this IWebHostBuilder host, AppConfig config, CertificateProvider certificates)
    {
        host.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = (long)Math.Max(config.Server.MaxUploadMb, 1) * 1024 * 1024 + 4 * 1024 * 1024;
            kestrel.ListenAnyIP(config.Server.WebPort, listen =>
            {
                if (config.Server.WebHttps && certificates.Current is not null)
                {
                    listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.Current);
                }
            });
        });
    }
}

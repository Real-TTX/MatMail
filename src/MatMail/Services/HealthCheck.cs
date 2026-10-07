using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>
/// <c>dotnet MatMail.dll --healthcheck</c>: asks the running instance for /healthz (the container health check, so the image
/// needs no curl). Exit code 0 = healthy.
/// </summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        AppConfig config = AppConfigLoader.Load(AppConfigLoader.ResolveDataDir());
        string scheme = config.Server.WebHttps ? "https" : "http";

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{scheme}://127.0.0.1:{config.Server.WebPort}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

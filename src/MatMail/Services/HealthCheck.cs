using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>
/// <c>dotnet MatMail.dll --healthcheck</c>: asks the running instance for <c>/api/health</c>, the health of the Mat platform contract (the
/// container health check, so the image needs no curl). Exit code 0 = healthy: the answer is 200 for <c>ok</c> and <c>degraded</c> (a degraded
/// server must not be restarted) and 503 for <c>down</c>.
/// </summary>
public static class HealthCheck
{
    public const string Path = "/api/health";

    public static async Task<int> RunAsync()
    {
        AppConfig config = AppConfigLoader.Load(AppConfigLoader.ResolveDataDir());
        string scheme = config.Server.WebHttps ? "https" : "http";

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{scheme}://127.0.0.1:{config.Server.WebPort}{Path}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

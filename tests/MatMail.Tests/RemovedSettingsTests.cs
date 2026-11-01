using MatMail.Configuration;

namespace MatMail.Tests;

/// <summary>Settings that were taken out (<c>Server.WebHttps</c>, <c>Server.TrustProxyHeaders</c>) may still stand in the <c>app.json</c> of an installation: they are ignored.</summary>
public class RemovedSettingsTests
{
    [Fact]
    public void An_old_settings_file_with_settings_that_no_longer_exist_still_loads()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "matmail-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dataDir, "config"));
            File.WriteAllText(
                Path.Combine(dataDir, "config", "app.json"),
                """{ "Server": { "Hostname": "mail.old.example", "WebPort": 9933, "WebHttps": true, "TrustProxyHeaders": true, "MaxUploadMb": 12 } }""");

            AppConfig config = AppConfigLoader.Load(dataDir);

            Assert.Equal("mail.old.example", config.Server.Hostname);
            Assert.Equal(12, config.Server.MaxUploadMb);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MatMail.Configuration;

public class DatabaseConfig
{
    public string Host { get; set; } = "db";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "matmail";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "matmail";

    // "GSS Encryption Mode=Disable": no Kerberos library is shipped in the image, and none is wanted.
    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};Maximum Pool Size=100;GSS Encryption Mode=Disable";
}

public class DisplayConfig
{
    /// <summary>IANA time zone used for every date shown in the UI. Falls back to UTC when unknown.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>UI language when nothing else is known (no choice of the user, no browser preference): "en-US" or "de-DE".</summary>
    public string Culture { get; set; } = "en-US";

    /// <summary>Default theme for users without a choice: system, light or dark.</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>Default accent: blue, green, violet, teal, amber, rose or graphite.</summary>
    public string ThemeAccent { get; set; } = "blue";
}

public class ServerConfig
{
    /// <summary>Public host name of this server: used for the SMTP/IMAP banners and the generated certificate.</summary>
    public string Hostname { get; set; } = "localhost";

    /// <summary>Port of the web interface inside the container.</summary>
    public int WebPort { get; set; } = 9933;

    /// <summary>
    /// Serve the web interface over HTTPS itself (self-signed unless a certificate is configured). Off by default: MatMail is made to run
    /// behind a reverse proxy that terminates TLS. The mail servers use the certificate in either case.
    /// </summary>
    public bool WebHttps { get; set; }

    /// <summary>Trust X-Forwarded-For / X-Forwarded-Proto of a reverse proxy.</summary>
    public bool TrustProxyHeaders { get; set; }

    /// <summary>Largest attachment upload / message the web client accepts.</summary>
    public int MaxUploadMb { get; set; } = 50;
}

public class TlsConfig
{
    /// <summary>Folder searched for certificates. Empty = {data}/certs.</summary>
    public string? CertificateDirectory { get; set; }

    /// <summary>PEM certificate chain (default: {certs}/fullchain.pem).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>PEM private key (default: {certs}/privkey.pem).</summary>
    public string? KeyPath { get; set; }

    /// <summary>Alternative to PEM: a PKCS#12 file (default: {certs}/server.pfx).</summary>
    public string? PfxPath { get; set; }
    public string? PfxPassword { get; set; }

    /// <summary>Create a self-signed certificate when no certificate is found, so everything is encrypted from the first start.</summary>
    public bool GenerateSelfSigned { get; set; } = true;
}

public class SmtpConfig
{
    public bool Enabled { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port 25: mail from other servers and relay for trusted networks. 0 = off.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Port 587: mail clients (STARTTLS, sign-in). 0 = off.</summary>
    public int SubmissionPort { get; set; } = 587;

    /// <summary>Port 465: mail clients with TLS from the first byte. 0 = off.</summary>
    public int ImplicitTlsPort { get; set; } = 465;
    public int MaxMessageSizeMb { get; set; } = 50;
    public int MaxRecipients { get; set; } = 100;

    /// <summary>Refuse sign-in on connections that are not encrypted.</summary>
    public bool RequireTlsForAuth { get; set; } = true;
    public int MaxConnectionsPerIp { get; set; } = 30;
}

public class ImapConfig
{
    public bool Enabled { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port 143 (STARTTLS). 0 = off.</summary>
    public int Port { get; set; } = 143;

    /// <summary>Port 993 (TLS from the first byte). 0 = off.</summary>
    public int ImplicitTlsPort { get; set; } = 993;

    /// <summary>Refuse LOGIN until the connection is encrypted.</summary>
    public bool RequireTls { get; set; } = true;
    public int MaxConnectionsPerIp { get; set; } = 50;

    /// <summary>Open connections of all clients together (every session keeps buffers); further clients are turned away. 0 = no limit.</summary>
    public int MaxConnections { get; set; } = 500;
}

public class SyncConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>How many provider accounts are synchronised at the same time.</summary>
    public int MaxParallel { get; set; } = 4;
}

public class QueueConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes to wait before attempt 2, 3, ...; after the last entry the message is retried at that interval until it is too old.</summary>
    public int[] RetryMinutes { get; set; } = { 5, 15, 60, 240, 720 };
    public int MaxAgeHours { get; set; } = 72;

    /// <summary>Deliver straight to the recipient's MX when no provider account applies.</summary>
    public bool AllowDirectDelivery { get; set; } = true;
}

public class PushConfig
{
    /// <summary>Notifications on phones and desktops when mail arrives (Web Push). Off: the switch disappears from the account page.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Who the push services can contact about this server: a mailto: or https: address. Empty = postmaster@ the host name.</summary>
    public string? Contact { get; set; }
}

public class RetentionConfig
{
    public int ActivityLogDays { get; set; } = 60;
    public int TrashDays { get; set; } = 30;
    public int JunkDays { get; set; } = 30;
    public int SentQueueDays { get; set; } = 14;

    /// <summary>How long the mail transfer log keeps its lines. 0 = no transfer log at all.</summary>
    public int TransferLogDays { get; set; } = 30;

    /// <summary>Whether the transfer log shows the subject of a message. Off: administrators see who wrote to whom and when, not what about.</summary>
    public bool TransferLogSubjects { get; set; } = true;
}

/// <summary>
/// Installation-wide configuration. Lives as JSON in the data volume (<c>config/app.json</c>); every value can be
/// overridden with an environment variable <c>MATMAIL__Section__Key</c> (e.g. <c>MATMAIL__Server__Hostname</c>).
/// </summary>
public class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
    public TlsConfig Tls { get; set; } = new();
    public SmtpConfig Smtp { get; set; } = new();
    public ImapConfig Imap { get; set; } = new();
    public SyncConfig Sync { get; set; } = new();
    public QueueConfig Queue { get; set; } = new();
    public PushConfig Push { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();
}

public static class AppConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly object SaveLock = new();

    public static string ConfigPath(string dataDir) => Path.Combine(dataDir, "config", "app.json");

    /// <summary>The data directory: env MATMAIL_DATA, else /data in a container, else ./data.</summary>
    public static string ResolveDataDir()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("MATMAIL_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        bool inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        return inContainer ? "/data" : Path.GetFullPath("data");
    }

    /// <summary>
    /// Reads app.json (creating it with defaults on the first start) and applies the MATMAIL__ environment overrides.
    /// Sections added by a newer version are written back with their defaults, so they show up in the file.
    /// </summary>
    public static AppConfig Load(string dataDir)
    {
        Directory.CreateDirectory(Path.Combine(dataDir, "config"));
        string path = ConfigPath(dataDir);

        AppConfig fileConfig = LoadFile(dataDir);
        string normalized = JsonSerializer.Serialize(fileConfig, SerializerOptions);
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (!string.Equals(normalized, existing?.Trim(), StringComparison.Ordinal))
        {
            try
            {
                File.WriteAllText(path, normalized);
            }
            catch (IOException)
            {
                // read-only config mount: keep running with the values we loaded
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MATMAIL__")
            .Build();
        return configuration.Get<AppConfig>() ?? new AppConfig();
    }

    /// <summary>The configuration exactly as stored in the file (without environment overrides).</summary>
    public static AppConfig LoadFile(string dataDir)
    {
        string path = ConfigPath(dataDir);
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    /// <summary>Writes the file atomically (temp file + move), so a crash never leaves a half-written config.</summary>
    public static void Save(string dataDir, AppConfig config)
    {
        string path = ConfigPath(dataDir);
        string json = JsonSerializer.Serialize(config, SerializerOptions);
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>Where the active certificate comes from.</summary>
public enum CertificateSource
{
    Pfx,
    Pem,
    SelfSigned,
    None,
}

/// <summary>
/// Provides the TLS certificate for the web interface, SMTP and IMAP, so everything is encrypted from the outside.
/// Order: a PKCS#12 file, then a PEM chain + key (e.g. from certbot), then a self-signed certificate that is generated on the
/// first start. Certificate files are watched and re-read when they change (certificate renewal needs no restart).
/// </summary>
public sealed class CertificateProvider : IDisposable
{
    private readonly AppConfig _config;
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private X509Certificate2? _current;

    public CertificateProvider(AppConfig config, string dataDir, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _directory = string.IsNullOrWhiteSpace(config.Tls.CertificateDirectory) ? Path.Combine(dataDir, "certs") : config.Tls.CertificateDirectory;
        Directory.CreateDirectory(_directory);
        Reload();
        StartWatching();
    }

    public string CertificateDirectory => _directory;
    public CertificateSource Source { get; private set; } = CertificateSource.None;

    /// <summary>The certificate to present. Null only when no certificate exists and generating one is disabled.</summary>
    public X509Certificate2? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Reads the certificate again (also called when the files change).</summary>
    public void Reload()
    {
        lock (_lock)
        {
            try
            {
                X509Certificate2? loaded = LoadPfx();
                CertificateSource source = CertificateSource.Pfx;
                if (loaded is null)
                {
                    loaded = LoadPem();
                    source = CertificateSource.Pem;
                }

                if (loaded is null && _config.Tls.GenerateSelfSigned)
                {
                    loaded = LoadOrCreateSelfSigned();
                    source = CertificateSource.SelfSigned;
                }

                if (loaded is not null)
                {
                    _current?.Dispose();
                    _current = loaded;
                    Source = source;
                    _logger.LogInformation("TLS certificate: {Subject}, valid until {NotAfter:u} ({Source}).", loaded.Subject, loaded.NotAfter, source);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The TLS certificate could not be loaded; keeping the previous one.");
            }
        }
    }

    /// <summary>One line per fact for the settings page.</summary>
    public CertificateInfo? Describe()
    {
        X509Certificate2? certificate = Current;
        if (certificate is null)
        {
            return null;
        }

        return new CertificateInfo(
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            certificate.Thumbprint,
            Source,
            string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal));
    }

    private X509Certificate2? LoadPfx()
    {
        string path = Resolve(_config.Tls.PfxPath, "server.pfx");
        if (!File.Exists(path))
        {
            return null;
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, _config.Tls.PfxPassword, KeyStorage);
    }

    private X509Certificate2? LoadPem()
    {
        string certPath = Resolve(_config.Tls.CertificatePath, "fullchain.pem");
        string keyPath = Resolve(_config.Tls.KeyPath, "privkey.pem");
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return null;
        }

        using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        // SslStream on Windows needs a key it can use; re-import through PKCS#12 so this works everywhere.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null, KeyStorage);
    }

    private X509Certificate2 LoadOrCreateSelfSigned()
    {
        string path = Path.Combine(_directory, "selfsigned.pfx");
        string host = _config.Server.Hostname;

        if (File.Exists(path))
        {
            X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(path, null, KeyStorage);
            bool sameHost = existing.Subject.Equals($"CN={host}", StringComparison.OrdinalIgnoreCase);
            if (sameHost && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
            {
                return existing;
            }

            existing.Dispose();
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            names.AddDnsName("localhost");
        }

        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(825));
        byte[] pfx = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);
        _logger.LogWarning("No TLS certificate found: generated a self-signed one for {Host}. Put your own certificate into {Directory} to replace it.", host, _directory);
        return X509CertificateLoader.LoadPkcs12(pfx, null, KeyStorage);
    }

    /// <summary>
    /// Private keys stay in memory only (EphemeralKeySet) where TLS servers can use such keys. Windows (SChannel) cannot ("the
    /// platform does not support ephemeral keys"), so there the key goes into a temporary key container that is removed again
    /// when the certificate is disposed.
    /// </summary>
    private static X509KeyStorageFlags KeyStorage => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.Exportable
        : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    private string Resolve(string? configured, string defaultFileName)
        => string.IsNullOrWhiteSpace(configured) ? Path.Combine(_directory, defaultFileName) : configured;

    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(_directory) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler handler = (_, e) =>
            {
                if (e.Name is not null && e.Name.StartsWith("selfsigned", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Renewal tools write several files; wait until they are done.
                _debounce?.Dispose();
                _debounce = new Timer(_ => Reload(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            };
            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.Renamed += (s, e) => handler(s, e);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Certificate files are not watched; restart to pick up a renewed certificate.");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _current?.Dispose();
    }
}

public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Thumbprint,
    CertificateSource Source,
    bool IsSelfSigned);

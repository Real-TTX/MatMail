using System.Net;
using MatMail.Configuration;
using MatMail.Data;

namespace MatMail.Services;

/// <summary>
/// The public name of the server (<c>Server.Hostname</c>): the name in the settings for mail programs, in the greeting of the IMAP and SMTP
/// servers, in the HELO towards other mail servers and in the self-signed certificate. Out of the box it is <c>localhost</c>, which is right for
/// none of them. So it is learned, once, from the address under which a platform administrator opens the web interface (the proxy passes the
/// name through): the first such request that carries a real public name sets it, saves it to <c>app.json</c>, makes the self-signed certificate
/// for it and says so in the activity log. A name set on purpose (the server settings, <c>MATMAIL__Server__Hostname</c>) is never replaced.
/// </summary>
public sealed class ServerName(AppConfig config, ActivityLogger log, CertificateProvider? certificates = null)
{
    private static readonly string[] PrivateSuffixes = [".localhost", ".local", ".lan", ".internal", ".home.arpa", ".test", ".example", ".invalid"];
    private readonly object _lock = new();

    /// <summary>The name as configured.</summary>
    public string Configured => config.Server.Hostname;

    /// <summary>No name has been given yet: empty, <c>localhost</c>, an IP address or a name without a dot.</summary>
    public bool IsUnset => !IsPublicName(config.Server.Hostname);

    /// <summary>
    /// The name to show to the people who set up a mail program: the configured one, or – while none is set – the name under which this
    /// request reached the server, if that is a public one.
    /// </summary>
    public string ForRequest(HttpRequest request) => IsUnset && PublicNameOf(request.Host) is { } seen ? seen : config.Server.Hostname;

    /// <summary>Whether a name can be the public name of a mail server: a DNS name with a dot, not an IP address, not a private or test name.</summary>
    public static bool IsPublicName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name.Trim('[', ']'), out _))
        {
            return false;
        }

        string host = name.Trim().TrimEnd('.').ToLowerInvariant();
        return host.Contains('.')
               && Uri.CheckHostName(host) == UriHostNameType.Dns
               && !PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>The host of a request without its port, if it is a public name.</summary>
    public static string? PublicNameOf(HostString host)
        => host.HasValue && IsPublicName(host.Host) ? host.Host.Trim().TrimEnd('.').ToLowerInvariant() : null;

    /// <summary>A name set on purpose (the server settings, which save it to app.json): it holds from now on, the self-signed certificate is made for it.</summary>
    public void Apply(string name)
    {
        string host = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == config.Server.Hostname)
        {
            return;
        }

        lock (_lock)
        {
            config.Server.Hostname = host;
        }

        certificates?.Reload();
    }

    /// <summary>Takes the name of this request as the server's name, if none is set yet and the request carries a public one. Returns whether it did.</summary>
    public async Task<bool> LearnFromAsync(HttpRequest request, long? userId)
    {
        if (!IsUnset || PublicNameOf(request.Host) is not { } name)
        {
            return false;
        }

        lock (_lock)
        {
            if (!IsUnset)
            {
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(config.DataDir))
                {
                    throw new IOException("No data directory.");
                }

                AppConfig file = AppConfigLoader.LoadFile(config.DataDir);
                file.Server.Hostname = name;
                AppConfigLoader.Save(config.DataDir, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a read-only configuration: the name still holds until the next start
            }

            config.Server.Hostname = name;
        }

        certificates?.Reload();   // a self-signed certificate is made again for the new name (one of your own stays as it is)
        await log.InfoAsync(
            ActivityCategory.System,
            $"The name of the server is now {name}: it was still localhost, and a platform administrator opened the web interface under this name. Change it under Server settings if that is not right.",
            userId: userId);
        return true;
    }
}

/// <summary>Lets <see cref="ServerName"/> learn the name from the requests of platform administrators while none is set.</summary>
public sealed class ServerNameMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ServerName serverName, CurrentUser current)
    {
        if (serverName.IsUnset && current.IsSystemAdmin && HttpMethods.IsGet(http.Request.Method))
        {
            await serverName.LearnFromAsync(http.Request, current.UserId);
        }

        await next(http);
    }
}

using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;

namespace MatMail.Tests.Support;

/// <summary>
/// The test classes that start the whole app in memory hand their settings to <c>Program</c> through environment variables (it reads them
/// before the host is built), so they run one after another in this collection, and not next to any other test (another one that sets
/// a variable Program reads, such as MATMAIL_RESTORE_FROM, must not meet a starting app).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlatformWebCollection
{
    public const string Name = "Platform web";
}

/// <summary>
/// The whole application in memory (<see cref="WebApplicationFactory{TEntryPoint}"/>) on a database of its own on the server of
/// <c>MATMAIL_TEST_DB</c> (the app creates it, the factory drops it) and an empty data folder, with the first administrator from the
/// environment (<see cref="AdminUser"/>). The mail servers, the synchronisation, the queue worker and the backups stay off.
/// <see cref="ApiKey"/> is <c>MAT_API_KEY</c> unless <c>withKey</c> is false; with an <c>authority</c> the sign-in through MatIdentity is on, with a
/// fixed OpenID configuration so that no MatIdentity has to run (the tests look at the redirects).
/// </summary>
public class PlatformFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key-0123456789";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin-Passw0rd!";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matmail-platform-" + Guid.NewGuid().ToString("N"));
    private readonly string _database = "matmail_p_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string? _authority;
    private readonly bool _reachable;

    public PlatformFactory()
        : this(withKey: true, authority: null)
    {
    }

    protected PlatformFactory(bool withKey, string? authority, bool reachable = true)
    {
        _authority = authority;
        _reachable = reachable;

        string? admin = TestDatabase.AdminConnectionString;
        if (admin is not null)
        {
            var server = new NpgsqlConnectionStringBuilder(admin);
            Set("MATMAIL__Database__Host", server.Host ?? "localhost");
            Set("MATMAIL__Database__Port", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("MATMAIL__Database__Username", server.Username ?? "postgres");
            Set("MATMAIL__Database__Password", server.Password ?? string.Empty);
            Set("MATMAIL__Database__Database", _database);
        }

        Set("MATMAIL_DATA", _data);
        Set("MATMAIL__Smtp__Enabled", "false");
        Set("MATMAIL__Imap__Enabled", "false");
        Set("MATMAIL__Sync__Enabled", "false");
        Set("MATMAIL__Queue__Enabled", "false");
        Set("MATMAIL__Backup__Enabled", "false");
        Set("MATMAIL__Directories__SyncMinutes", "0");
        Set("MATMAIL__Display__Culture", "en-US");
        Set("MATMAIL_ADMIN_USER", AdminUser);
        Set("MATMAIL_ADMIN_PASSWORD", AdminPassword);

        Set(MatPlatformSettings.ApiKeyVariable, withKey ? ApiKey : null);
        Set(MatPlatformSettings.AuthorityVariable, authority);
        Set(MatPlatformSettings.ClientIdVariable, null);
        Set(MatPlatformSettings.ClientSecretVariable, authority is null ? null : "test-secret");
        Set(MatPlatformSettings.InternalUrlVariable, null);
    }

    public string DataDir => _data;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        if (_authority is not null && _reachable)
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = _authority,
                AuthorizationEndpoint = _authority + "/connect/authorize",
                TokenEndpoint = _authority + "/connect/token",
                EndSessionEndpoint = _authority + "/connect/logout",
            };

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
        }
    }

    /// <summary>A client that keeps cookies and does not follow redirects (the tests look at each step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public HttpClient Api()
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        NpgsqlConnection.ClearAllPools();
        foreach (string name in _variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (TestDatabase.AdminConnectionString is { } admin)
        {
            try
            {
                using var connection = new NpgsqlConnection(admin);
                connection.Open();
                using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                Console.Error.WriteLine($"Test database {_database} was not dropped: {ex.Message}");
            }
        }

        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaned up by the system later.
        }
    }

    private readonly List<string> _variables = [];

    private void Set(string name, string? value)
    {
        _variables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Without <c>MAT_API_KEY</c>: the protected endpoints answer 401 to everyone.</summary>
public sealed class NoKeyPlatformFactory() : PlatformFactory(withKey: false, authority: null);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not need to run).</summary>
public sealed class IdentityPlatformFactory() : PlatformFactory(withKey: true, authority: Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> pointing at an address where nothing answers (MatIdentity is down).</summary>
public sealed class UnreachableIdentityPlatformFactory() : PlatformFactory(withKey: true, authority: "http://127.0.0.1:9", reachable: false);

using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MatMail.Tests.Support;

/// <summary>
/// Tests that need PostgreSQL read <c>MATMAIL_TEST_DB</c> (an admin connection string, e.g.
/// <c>Host=localhost;Port=15432;Database=postgres;Username=postgres;Password=matmail</c> with the dev stack running).
/// Without it they are skipped.
/// </summary>
public static class TestDatabase
{
    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("MATMAIL_TEST_DB");
    public static bool Available => !string.IsNullOrWhiteSpace(AdminConnectionString);
}

public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!TestDatabase.Available)
        {
            Skip = "MATMAIL_TEST_DB is not set (needs a PostgreSQL server).";
        }
    }
}

public sealed class DbTheoryAttribute : TheoryAttribute
{
    public DbTheoryAttribute()
    {
        if (!TestDatabase.Available)
        {
            Skip = "MATMAIL_TEST_DB is not set (needs a PostgreSQL server).";
        }
    }
}

/// <summary>The rows most tests start from.</summary>
public sealed record Seed(Tenant Tenant, User Alice, Mailbox AliceMailbox, User Bob, Mailbox BobMailbox, Mailbox Info, long UnassignedMailboxId);

/// <summary>
/// A complete application service container on a throw-away database (created, migrated and dropped per test class instance),
/// wired exactly like the real start-up.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string Domain = "example.test";

    private readonly string _adminConnection;
    private readonly string _databaseName;

    private TestHost(ServiceProvider services, string adminConnection, string databaseName, AppConfig config)
    {
        Services = services;
        _adminConnection = adminConnection;
        _databaseName = databaseName;
        Config = config;
    }

    public ServiceProvider Services { get; }
    public AppConfig Config { get; }
    public string ConnectionString => Config.Database.ConnectionString;

    public static async Task<TestHost> CreateAsync(Action<AppConfig>? configure = null)
    {
        string admin = TestDatabase.AdminConnectionString ?? throw new InvalidOperationException("MATMAIL_TEST_DB is not set.");
        var builder = new NpgsqlConnectionStringBuilder(admin);
        string databaseName = "matmail_t_" + Guid.NewGuid().ToString("N")[..12];

        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var config = new AppConfig
        {
            Database = new DatabaseConfig
            {
                Host = builder.Host ?? "localhost",
                Port = builder.Port,
                Database = databaseName,
                Username = builder.Username ?? "postgres",
                Password = builder.Password ?? string.Empty,
            },
        };
        config.Server.Hostname = "mail.example.test";
        configure?.Invoke(config);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMatMailServices(config);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        using (IServiceScope scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.MigrateAsync();
        }

        return new TestHost(provider, admin, databaseName, config);
    }

    /// <summary>A scope acting as the system (no tenant restriction) unless told otherwise.</summary>
    public IServiceScope Scope(Action<CurrentUser>? actor = null)
    {
        IServiceScope scope = Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<CurrentUser>();
        if (actor is null)
        {
            current.RunAsSystem();
        }
        else
        {
            actor(current);
        }

        return scope;
    }

    /// <summary>A scope acting as the given user of their tenant (like an IMAP/SMTP session after sign-in).</summary>
    public IServiceScope ScopeAs(User user, params string[] permissions)
        => Scope(c => c.RunAs(user.Id, user.TenantId, user.IsSystemAdmin, permissions.Length == 0 ? new[] { Permissions.MailUse } : permissions, user.DisplayName));

    /// <summary>One tenant "Home" with the domain example.test, users alice and bob (each with a mailbox and address) and a shared mailbox info@.</summary>
    public async Task<Seed> SeedAsync()
    {
        using IServiceScope scope = Scope();
        var tenants = scope.ServiceProvider.GetRequiredService<TenantService>();
        (Tenant? tenant, string? error) = await tenants.CreateAsync("Home", null);
        Assert.NotNull(tenant);
        Assert.Null(error);

        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.Domains.Add(new Domain { TenantId = tenant.Id, Name = Domain });
        await db.SaveChangesAsync();

        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var mailboxes = scope.ServiceProvider.GetRequiredService<MailboxService>();
        long userRole = await db.Roles.Where(r => r.TenantId == tenant.Id && r.Name == TenantService.UserRoleName).Select(r => r.Id).FirstAsync();

        async Task<(User, Mailbox)> CreateUserAsync(string login, string name)
        {
            (User? user, string? userError) = await users.CreateAsync(
                new UserInput { LoginName = login, DisplayName = name, Password = "Test-Passw0rd!", RoleIds = new[] { userRole }, CreateMailbox = true, PrimaryAddress = $"{login}@{Domain}" },
                tenant.Id);
            Assert.Null(userError);
            Mailbox mailbox = await db.Mailboxes.FirstAsync(m => m.OwnerUserId == user!.Id);
            return (user!, mailbox);
        }

        (User alice, Mailbox aliceBox) = await CreateUserAsync("alice", "Alice");
        (User bob, Mailbox bobBox) = await CreateUserAsync("bob", "Bob");

        Mailbox info = await mailboxes.CreateMailboxAsync("Info", MailboxType.Shared, null, tenant.Id);
        Assert.Null(await mailboxes.AddAddressAsync(info, $"info@{Domain}", isPrimary: true));
        Mailbox unassigned = await mailboxes.GetUnassignedMailboxAsync(tenant.Id);
        return new Seed(tenant, alice, aliceBox, bob, bobBox, info, unassigned.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

public static class RawMail
{
    /// <summary>A small plain-text message with CRLF line ends.</summary>
    public static byte[] Build(string from, string to, string subject, string body, string? messageId = null, string? extraHeaders = null, string? cc = null)
    {
        string id = messageId ?? $"<{Guid.NewGuid():N}@sender.test>";
        string text =
            $"From: {from}\r\nTo: {to}\r\n{(cc is null ? string.Empty : $"Cc: {cc}\r\n")}Subject: {subject}\r\nMessage-ID: {id}\r\nDate: Tue, 07 Oct 2026 10:00:00 +0200\r\n" +
            $"MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n{extraHeaders}\r\n{body}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}

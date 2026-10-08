using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatMail.Services;

/// <summary>Work that happens once at start-up: bring the database up to date and, if asked, create the first administrator.</summary>
public static class StartupInitializer
{
    /// <summary>
    /// Applies the migrations. PostgreSQL may still be starting (a first start initialises its data directory first) and may not
    /// have the database yet, so the server is waited for and the database created quietly first; what then still fails is retried.
    /// </summary>
    public static async Task MigrateDatabaseAsync(IServiceProvider services, ILogger logger)
    {
        using (IServiceScope scope = services.CreateScope())
        {
            string? connectionString = scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.GetConnectionString();
            await EnsureDatabaseExistsAsync(connectionString, logger);
        }

        const int maxAttempts = 20;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using IServiceScope scope = services.CreateScope();
                scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
                var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
                logger.LogInformation("Applying database migrations (attempt {Attempt}/{Max}).", attempt, maxAttempts);
                await db.Database.MigrateAsync();
                logger.LogInformation("Database is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("Migration attempt {Attempt}/{Max} failed: {Message}", attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    /// <summary>
    /// Waits until the PostgreSQL server answers and creates the database when the server has none of that name: a fresh container
    /// only has "postgres", so the stack needs neither a health check nor <c>POSTGRES_DB</c>. EF Core would manage both as well, but
    /// logs an error for every failed probe, which looks like a failure on every first start. Anything else that goes wrong here (no
    /// right to create databases, no access to the maintenance database, a server that stays away) is left to the migration, which
    /// decides as before. Never throws.
    /// </summary>
    /// <param name="maxWait">How long a server that is not there yet is waited for (default: 90 seconds).</param>
    public static async Task EnsureDatabaseExistsAsync(string? connectionString, ILogger logger, TimeSpan? maxWait = null)
    {
        DateTime giveUp = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(90));
        bool announced = false;
        while (true)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString);
                string? name = builder.Database;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                string host = $"{builder.Host}:{builder.Port}";
                builder.Database = "postgres";
                builder.Pooling = false;
                try
                {
                    await CreateDatabaseWhenMissingAsync(builder.ConnectionString, name, logger);
                    return;
                }
                catch (Exception ex) when (IsServerNotReady(ex) && DateTime.UtcNow < giveUp)
                {
                    if (!announced)
                    {
                        logger.LogInformation("Waiting for PostgreSQL at {Host} …", host);
                        announced = true;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check whether the database exists; the migration decides.");
                return;
            }
        }
    }

    private static async Task CreateDatabaseWhenMissingAsync(string maintenanceConnectionString, string name, ILogger logger)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", name);
        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        // An identifier cannot be a parameter: it is quoted.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name.Replace("\"", "\"\"")}\"", connection);
        await create.ExecuteNonQueryAsync();
        logger.LogInformation("Created the database '{Database}'.", name);
    }

    /// <summary>Connection refused, host not known yet, "the database system is starting up", timeouts: it may work in a moment.</summary>
    internal static bool IsServerNotReady(Exception ex) => ex is NpgsqlException { IsTransient: true } || ex is TimeoutException;

    /// <summary>
    /// Unattended installation: with MATMAIL_ADMIN_USER and MATMAIL_ADMIN_PASSWORD set (and no user yet) the first tenant and
    /// administrator are created without the setup page. MATMAIL_TENANT and MATMAIL_ADMIN_NAME are optional.
    /// </summary>
    public static async Task SeedAdministratorFromEnvironmentAsync(IServiceProvider services, ILogger logger)
    {
        string? user = Environment.GetEnvironmentVariable("MATMAIL_ADMIN_USER");
        string? password = Environment.GetEnvironmentVariable("MATMAIL_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            return;
        }

        using IServiceScope scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        if (await signIn.AnyUsersExistAsync())
        {
            return;
        }

        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        string? error = await setup.CreateFirstAdministratorAsync(
            Environment.GetEnvironmentVariable("MATMAIL_TENANT") ?? "Home",
            Environment.GetEnvironmentVariable("MATMAIL_ADMIN_NAME") ?? user,
            user,
            null,
            password);

        if (error is null)
        {
            logger.LogInformation("Created the first administrator '{User}' from the environment.", user);
        }
        else
        {
            logger.LogError("The administrator from the environment could not be created: {Error}", error);
        }
    }
}

/// <summary>Creates the first tenant and its administrator (setup page and unattended start).</summary>
public sealed class SetupService
{
    private readonly TenantService _tenants;
    private readonly UserService _users;
    private readonly MatMailDbContext _db;
    private readonly SetupState _state;
    private readonly CurrentUser _current;

    public SetupService(TenantService tenants, UserService users, MatMailDbContext db, SetupState state, CurrentUser current)
    {
        _tenants = tenants;
        _users = users;
        _db = db;
        _state = state;
        _current = current;
    }

    /// <summary>Returns an error text (English source string) or null when everything was created.</summary>
    public async Task<string?> CreateFirstAdministratorAsync(string tenantName, string displayName, string loginName, string? mailAddress, string password)
    {
        if (await _db.Users.IgnoreQueryFilters().AnyAsync())
        {
            return "The installation is already set up.";
        }

        // Nobody is signed in yet: the very first administrator is created on behalf of the system.
        _current.RunAsSystem();

        (Tenant? tenant, string? tenantError) = await _tenants.CreateAsync(tenantName, null);
        if (tenant is null)
        {
            return tenantError;
        }

        long administratorRoleId = await _db.Roles.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenant.Id && r.Name == TenantService.AdministratorRoleName)
            .Select(r => r.Id)
            .FirstAsync();

        (User? user, string? error) = await _users.CreateAsync(
            new UserInput
            {
                LoginName = loginName,
                DisplayName = displayName,
                Password = password,
                IsSystemAdmin = true,
                RoleIds = new[] { administratorRoleId },
                CreateMailbox = !string.IsNullOrWhiteSpace(mailAddress),
                PrimaryAddress = mailAddress,
            },
            tenant.Id);

        if (user is not null)
        {
            _state.HasUsers = true;
        }

        return error;
    }
}

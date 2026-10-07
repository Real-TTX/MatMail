using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>Work that happens once at start-up: bring the database up to date and, if asked, create the first administrator.</summary>
public static class StartupInitializer
{
    /// <summary>Applies the migrations; retries for a while because PostgreSQL may still be starting.</summary>
    public static async Task MigrateDatabaseAsync(IServiceProvider services, ILogger logger)
    {
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

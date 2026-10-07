using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MatMail.Tests;

/// <summary>The proof between the password page and the code page.</summary>
public class TwoFactorTicketTests
{
    private static TwoFactorTicket NewTicket(IDataProtectionProvider? provider = null) => new(provider ?? new EphemeralDataProtectionProvider());

    private static readonly PendingSignIn Pending = new(42, Remember: true, "/Mail?folder=3", "eine-firma");

    /// <summary>What the browser sends back: the cookie the server set.</summary>
    private static HttpContext Browser(HttpContext responseOf)
    {
        string setCookie = responseOf.Response.Headers.SetCookie.ToString();
        string pair = setCookie.Split(';')[0];
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = pair;
        return next;
    }

    [Fact]
    public void A_pending_sign_in_travels_in_a_protected_http_only_strict_cookie()
    {
        TwoFactorTicket ticket = NewTicket();
        var issued = new DefaultHttpContext();
        issued.Request.Scheme = "https";

        ticket.Issue(issued, Pending);

        string cookie = issued.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(TwoFactorTicket.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/Account", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eine-firma", cookie);
        Assert.DoesNotContain("folder", cookie);

        Assert.Equal(Pending, ticket.Read(Browser(issued)));
    }

    [Fact]
    public void What_the_address_bar_sent_cannot_make_the_cookie_too_big()
    {
        TwoFactorTicket ticket = NewTicket();
        var issued = new DefaultHttpContext();

        ticket.Issue(issued, new PendingSignIn(7, Remember: false, "/" + new string('x', 5000), new string('y', 500)));

        string cookie = issued.Response.Headers.SetCookie.ToString();
        Assert.True(cookie.Length < 1000, "cookie of " + cookie.Length + " characters");
        Assert.Equal(new PendingSignIn(7, false, null, null), ticket.Read(Browser(issued)));
    }

    [Fact]
    public void Without_https_the_cookie_is_not_marked_secure_so_it_works_on_plain_http_setups()
    {
        var issued = new DefaultHttpContext();
        NewTicket().Issue(issued, Pending);

        string attributes = issued.Response.Headers.SetCookie.ToString().Split(';', 2)[1];
        Assert.DoesNotContain("secure", attributes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", attributes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tampered_ticket_is_worth_nothing()
    {
        TwoFactorTicket ticket = NewTicket();
        var issued = new DefaultHttpContext();
        ticket.Issue(issued, Pending);
        string pair = issued.Response.Headers.SetCookie.ToString().Split(';')[0];

        string value = pair[(pair.IndexOf('=') + 1)..];
        char flipped = value[^3] == 'A' ? 'B' : 'A';
        var tampered = new DefaultHttpContext();
        tampered.Request.Headers.Cookie = TwoFactorTicket.CookieName + "=" + value[..^3] + flipped + value[^2..];

        Assert.Null(ticket.Read(tampered));

        var garbage = new DefaultHttpContext();
        garbage.Request.Headers.Cookie = TwoFactorTicket.CookieName + "=not-a-ticket";
        Assert.Null(ticket.Read(garbage));
        Assert.Null(ticket.Read(new DefaultHttpContext()));
    }

    [Fact]
    public void A_ticket_made_for_another_installation_is_refused()
    {
        var issued = new DefaultHttpContext();
        NewTicket().Issue(issued, Pending);

        Assert.Null(NewTicket().Read(Browser(issued)));
    }

    [Fact]
    public async Task A_ticket_expires()
    {
        TwoFactorTicket ticket = NewTicket();
        var issued = new DefaultHttpContext();
        ticket.Issue(issued, Pending, TimeSpan.FromMilliseconds(200));
        HttpContext browser = Browser(issued);
        Assert.NotNull(ticket.Read(browser));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Null(ticket.Read(browser));
        Assert.Equal(TimeSpan.FromMinutes(5), TwoFactorTicket.Lifetime);
    }

    [Fact]
    public void Clearing_deletes_the_cookie()
    {
        var context = new DefaultHttpContext();
        NewTicket().Clear(context);

        string cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(TwoFactorTicket.CookieName + "=;", cookie);
        Assert.Contains("1970", cookie);
    }
}

/// <summary>Whoever must set up two-factor authentication is kept on the page where that happens.</summary>
public class TwoFactorSetupMiddlewareTests
{
    private static ClaimsPrincipal Principal(bool required, bool enabled, bool mustChangePassword = false)
        => SignInService.BuildPrincipal(
            new SessionSnapshot(1, 1, "Home", 1, "alice", "Alice", false, mustChangePassword, new[] { Permissions.MailUse }, null, null, null, null, null, null, true, DateTime.UtcNow.AddDays(1), enabled, required),
            Guid.NewGuid());

    private static async Task<(bool Reached, int Status, string? Location)> RunAsync(ClaimsPrincipal user, string path)
    {
        bool reached = false;
        var middleware = new TwoFactorSetupMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { User = user };
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        string? location = context.Response.Headers.Location.ToString();
        return (reached, context.Response.StatusCode, string.IsNullOrEmpty(location) ? null : location);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Mail")]
    [InlineData("/Admin")]
    [InlineData("/Admin/Users/Edit")]
    [InlineData("/Account")]
    [InlineData("/Account/Password")]
    [InlineData("/Account/Sessions")]
    [InlineData("/Account/MailPrograms")]
    [InlineData("/Account/Security/Elsewhere")]
    public async Task Everything_leads_to_the_set_up_page(string path)
    {
        (bool reached, int status, string? location) = await RunAsync(Principal(required: true, enabled: false), path);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.Equal("/Account/Security", location);
    }

    [Theory]
    [InlineData("/Account/Security")]
    [InlineData("/account/security")]
    [InlineData("/Account/Logout")]
    [InlineData("/Account/Language")]
    [InlineData("/healthz")]
    [InlineData("/Error")]
    [InlineData("/css/app.css")]
    [InlineData("/js/app.js")]
    [InlineData("/icons/logo.svg")]
    [InlineData("/brand/0123456789abcdef")]
    public async Task The_set_up_page_signing_out_and_what_a_page_is_made_of_stay_open(string path)
    {
        (bool reached, _, string? location) = await RunAsync(Principal(required: true, enabled: false), path);

        Assert.True(reached);
        Assert.Null(location);
    }

    [Theory]
    [InlineData("/api/mail/bootstrap")]
    [InlineData("/api/mail/messages/7/cid/logo.png")]
    [InlineData("/api/mail/messages/7/attachment/0")]
    public async Task The_mail_api_is_closed_with_a_plain_refusal_and_not_a_redirect(string path)
    {
        (bool reached, int status, string? location) = await RunAsync(Principal(required: true, enabled: false), path);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Null(location);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Nobody_else_is_held_back(bool required, bool enabled)
    {
        (bool reached, _, string? location) = await RunAsync(Principal(required, enabled), "/Mail");

        Assert.True(reached);
        Assert.Null(location);
    }

    [Fact]
    public async Task A_forced_password_change_comes_first_so_the_two_never_lead_in_circles()
    {
        (bool reached, _, string? location) = await RunAsync(Principal(required: true, enabled: false, mustChangePassword: true), "/Account/Password");

        Assert.True(reached);
        Assert.Null(location);
    }

    [Fact]
    public async Task Anonymous_requests_are_not_touched()
    {
        (bool reached, _, string? location) = await RunAsync(new ClaimsPrincipal(new ClaimsIdentity()), "/Account/Login");

        Assert.True(reached);
        Assert.Null(location);
    }
}

/// <summary>An installation that is already running is brought up to date without losing the administrators' rights.</summary>
public class TwoFactorMigrationTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DbFact]
    public async Task Existing_administrator_roles_get_the_new_permission_and_the_migration_can_be_undone()
    {
        const string before = "20261007220658_AddTenantBranding";
        using (IServiceScope scope = _host.Scope())
        {
            var migrator = scope.ServiceProvider.GetRequiredService<MatMailDbContext>().GetService<IMigrator>();
            await migrator.MigrateAsync(before);
        }

        // The state of an installation before this change: an Administrator role without the permission, a user role, a custom role.
        await ExecuteAsync(
            "INSERT INTO \"Tenant\" (\"Name\", \"IsActive\", \"CreateDate\", \"UpdateDate\") VALUES ('Old', true, now(), now());" +
            "INSERT INTO \"Role\" (\"TenantId\", \"Name\", \"IsBuiltIn\", \"Permissions\", \"CreateDate\", \"UpdateDate\") " +
            "SELECT \"Id\", 'Administrator', true, ARRAY['mail.use','users.manage'], now(), now() FROM \"Tenant\" WHERE \"Name\" = 'Old';" +
            "INSERT INTO \"Role\" (\"TenantId\", \"Name\", \"IsBuiltIn\", \"Permissions\", \"CreateDate\", \"UpdateDate\") " +
            "SELECT \"Id\", 'Helpers', false, ARRAY['mail.use'], now(), now() FROM \"Tenant\" WHERE \"Name\" = 'Old';");

        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().Database.MigrateAsync();
        }

        Dictionary<string, string[]> roles = await _host.ReadAsync(async db =>
            (await (from role in db.Roles.IgnoreQueryFilters().AsNoTracking()
                    join tenant in db.Tenants on role.TenantId equals tenant.Id
                    where tenant.Name == "Old"
                    select new { role.Name, role.Permissions }).ToListAsync()).ToDictionary(r => r.Name, r => r.Permissions));
        Assert.Equal(new[] { "mail.use", "users.manage", "security.manage" }, roles["Administrator"]);
        Assert.Equal(new[] { "mail.use" }, roles["Helpers"]);

        // The new columns start out harmless.
        Assert.All(await _host.ReadAsync(db => db.Tenants.AsNoTracking().Select(t => t.TwoFactorMode).ToListAsync()), mode => Assert.Equal(TwoFactorMode.Optional, mode));
        Assert.All(await _host.ReadAsync(db => db.Roles.IgnoreQueryFilters().AsNoTracking().Select(r => r.RequiresTwoFactor).ToListAsync()), flag => Assert.False(flag));

        // And back again: the permission leaves the roles, the tables go.
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().GetService<IMigrator>().MigrateAsync(before);
        }

        await using var connection = new NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT array_to_string(\"Permissions\", ',') FROM \"Role\" WHERE \"Name\" = 'Administrator' AND \"TenantId\" = (SELECT \"Id\" FROM \"Tenant\" WHERE \"Name\" = 'Old')", connection);
        Assert.Equal("mail.use,users.manage", await command.ExecuteScalarAsync());
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('UserTotp', 'UserRecoveryCode', 'AppPassword')", connection);
        Assert.Equal(0L, await tables.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

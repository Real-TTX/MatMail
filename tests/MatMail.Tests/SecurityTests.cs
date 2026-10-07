using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>Findings of the security reviews, kept as tests.</summary>
public class UserAdministrationSecurityTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private User _systemAdmin = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();

        using IServiceScope scope = _host.Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        (User? admin, string? error) = await users.CreateAsync(
            new UserInput { LoginName = "root", DisplayName = "Root", Password = "Test-Passw0rd!", IsSystemAdmin = true, CreateMailbox = false }, _seed.Tenant.Id);
        Assert.Null(error);
        _systemAdmin = admin!;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static UserInput InputOf(User user, string? password = null, bool isActive = true) => new()
    {
        LoginName = user.LoginName, DisplayName = user.DisplayName, IsActive = isActive, IsSystemAdmin = user.IsSystemAdmin, Password = password,
    };

    [DbFact]
    public async Task Someone_who_manages_users_cannot_change_or_delete_a_system_administrator()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice, Permissions.UsersManage, Permissions.MailUse);
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        Assert.Equal("Only system administrators can change a system administrator.", await users.UpdateAsync(_systemAdmin.Id, InputOf(_systemAdmin, "Another-Passw0rd!")));
        Assert.Equal("Only system administrators can change a system administrator.", await users.UpdateAsync(_systemAdmin.Id, InputOf(_systemAdmin, isActive: false)));
        Assert.Equal("Only system administrators can change a system administrator.", await users.DeleteAsync(_systemAdmin.Id, deleteMailbox: false));

        // The password still is the old one.
        using IServiceScope check = _host.Scope();
        SignInOutcome outcome = await check.ServiceProvider.GetRequiredService<SignInService>().ValidateCredentialsAsync("root", "Test-Passw0rd!");
        Assert.True(outcome.Succeeded);
    }

    [DbFact]
    public async Task The_last_active_system_administrator_cannot_be_deactivated()
    {
        using IServiceScope scope = _host.ScopeAs(_seed.Alice);
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(_systemAdmin.Id, _seed.Tenant.Id, isSystemAdmin: true, new[] { Permissions.UsersManage }, "Root");
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        // Root is the only system administrator; another one acting would be needed to switch root off.
        using IServiceScope other = _host.Scope();
        var otherUsers = other.ServiceProvider.GetRequiredService<UserService>();
        Assert.Equal("There must be at least one active system administrator.", await otherUsers.UpdateAsync(_systemAdmin.Id, InputOf(_systemAdmin, isActive: false)));
        Assert.Equal("There must be at least one active system administrator.", await otherUsers.DeleteAsync(_systemAdmin.Id, deleteMailbox: false));
        Assert.Equal("You cannot deactivate your own account.", await users.UpdateAsync(_systemAdmin.Id, InputOf(_systemAdmin, isActive: false)));
    }

    [DbFact]
    public async Task Nobody_hands_out_roles_with_rights_they_lack()
    {
        long administratorRole;
        long userRole;
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            administratorRole = await db.Roles.Where(r => r.TenantId == _seed.Tenant.Id && r.Name == TenantService.AdministratorRoleName).Select(r => r.Id).FirstAsync();
            userRole = await db.Roles.Where(r => r.TenantId == _seed.Tenant.Id && r.Name == TenantService.UserRoleName).Select(r => r.Id).FirstAsync();
        }

        // Alice may manage users but holds nothing else.
        using IServiceScope scope = _host.ScopeAs(_seed.Alice, Permissions.UsersManage, Permissions.MailUse);
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        UserInput input = InputOf(_seed.Bob);
        input.RoleIds = new[] { userRole, administratorRole };
        Assert.Null(await users.UpdateAsync(_seed.Bob.Id, input));

        using IServiceScope check = _host.Scope();
        var db2 = check.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long[] bobRoles = await db2.UserRoles.Where(ur => ur.UserId == _seed.Bob.Id).Select(ur => ur.RoleId).ToArrayAsync();
        Assert.Contains(userRole, bobRoles);
        Assert.DoesNotContain(administratorRole, bobRoles);
    }

    [DbFact]
    public async Task A_new_password_ends_the_other_sessions_of_that_user()
    {
        Guid own = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        using (IServiceScope setup = _host.Scope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MatMailDbContext>();
            foreach (Guid token in new[] { own, other })
            {
                db.UserSessions.Add(new UserSession
                {
                    Token = token, UserId = _seed.Bob.Id, TenantId = _seed.Tenant.Id, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _host.Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        Assert.Null(await users.UpdateAsync(_seed.Bob.Id, InputOf(_seed.Bob, "Fresh-Passw0rd-1"), keepSession: own));

        using IServiceScope check = _host.Scope();
        Guid[] remaining = await check.ServiceProvider.GetRequiredService<MatMailDbContext>().UserSessions
            .Where(s => s.UserId == _seed.Bob.Id).Select(s => s.Token).ToArrayAsync();
        Assert.Equal(new[] { own }, remaining);
    }
}

public class SignatureSecurityTests
{
    private static readonly SignatureContext Context = new("Max <b>Mustermann</b>", "max@example.test", "CEO", "+49 30 123", "Example GmbH");

    [Fact]
    public void Scripts_and_event_handlers_never_survive_the_rendering_of_a_signature()
    {
        string html = SignatureService.Render(
            "<p onmouseover=\"steal()\">Regards, {{DisplayName}}</p><script>alert(1)</script><img src=x onerror=\"alert(2)\"><a href=\"javascript:alert(3)\">link</a>",
            Context, html: true);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onmouseover", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Regards, Max &lt;b&gt;Mustermann&lt;/b&gt;", html);
    }

    [Fact]
    public void Harmless_formatting_and_links_are_kept()
    {
        string html = SignatureService.Render("<p><b>{{Tenant}}</b><br><a href=\"https://example.test/\">Website</a> · <a href=\"mailto:{{Email}}\">{{Email}}</a></p>", Context, html: true);

        Assert.Contains("<b>Example GmbH</b>", html);
        Assert.Contains("href=\"https://example.test/\"", html);
        Assert.Contains("mailto:max@example.test", html);
    }
}

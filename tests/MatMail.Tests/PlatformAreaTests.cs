using System.Net;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The platform area (<c>/Platform</c>, the lists across all tenants): only for system administrators, and they see every tenant.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class PlatformAreaTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    private const string SecondTenantDomain = "second-tenant.example";
    private const string FirstTenantDomain = "first-tenant.example";
    private static readonly string[] Lists = ["/Platform", "/Platform/Accounts", "/Platform/Mailboxes", "/Platform/Domains", "/Platform/Users"];

    private async Task<(long First, long Second)> SeedAsync()
    {
        using IServiceScope scope = site.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long first = await db.Tenants.OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();

        Tenant? second = await db.Tenants.FirstOrDefaultAsync(t => t.Name == "Second tenant");
        if (second is null)
        {
            (second, string? error) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Second tenant", null);
            Assert.Null(error);
            db.Domains.Add(new Domain { TenantId = first, Name = FirstTenantDomain });
            db.Domains.Add(new Domain { TenantId = second!.Id, Name = SecondTenantDomain });
            await db.SaveChangesAsync();

            // somebody who administers the first tenant and is no system administrator
            long role = await db.Roles.Where(r => r.TenantId == first && r.Name == TenantService.AdministratorRoleName).Select(r => r.Id).FirstAsync();
            (User? user, string? userError) = await scope.ServiceProvider.GetRequiredService<UserService>().CreateAsync(
                new UserInput { LoginName = "tenant-admin", DisplayName = "Tenant admin", Password = PlatformFactory.AdminPassword, RoleIds = new[] { role }, CreateMailbox = false }, first);
            Assert.Null(userError);
            Assert.NotNull(user);
        }

        return (first, second.Id);
    }

    private async Task<HttpClient> SignedInAsync(string login)
    {
        HttpClient browser = site.Browser();
        HttpResponseMessage response = await SignIn.PostAsync(browser, "/Account/Login", "/Account/Login", new Dictionary<string, string>
        {
            ["Input.LoginName"] = login,
            ["Input.Password"] = PlatformFactory.AdminPassword,
            ["Input.Remember"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    [DbFact]
    public async Task A_system_administrator_sees_the_lists_of_all_tenants_and_can_filter_by_tenant()
    {
        (long first, long second) = await SeedAsync();
        HttpClient admin = await SignedInAsync(PlatformFactory.AdminUser);

        foreach (string list in Lists)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(list)).StatusCode);
        }

        string all = await admin.GetStringAsync("/Platform/Domains");
        Assert.Contains(FirstTenantDomain, all);
        Assert.Contains(SecondTenantDomain, all);

        string onlySecond = await admin.GetStringAsync($"/Platform/Domains?TenantId={second}");
        Assert.Contains(SecondTenantDomain, onlySecond);
        Assert.DoesNotContain(FirstTenantDomain, onlySecond);
        string onlyFirst = await admin.GetStringAsync($"/Platform/Domains?TenantId={first}");
        Assert.Contains(FirstTenantDomain, onlyFirst);
        Assert.DoesNotContain(SecondTenantDomain, onlyFirst);

        // the overview names both, and a row switches the session by a POST
        string overview = await admin.GetStringAsync("/Platform");
        Assert.Contains("Second tenant", overview);
        Assert.Contains("action=\"/Account/SwitchTenant\"", overview);
    }

    [DbFact]
    public async Task Somebody_who_administers_a_tenant_and_nobody_else_gets_into_the_platform()
    {
        await SeedAsync();
        HttpClient tenantAdmin = await SignedInAsync("tenant-admin");
        Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync("/Admin")).StatusCode);   // the administration of their tenant is theirs

        foreach (string list in Lists.Concat(["/Admin/Tenants", "/Admin/Settings", "/Admin/Backup"]))
        {
            HttpResponseMessage response = await tenantAdmin.GetAsync(list);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(SecondTenantDomain, await response.Content.ReadAsStringAsync());
        }

        // and nobody signed in gets anything either
        HttpClient stranger = site.Browser();
        foreach (string list in Lists)
        {
            HttpResponseMessage response = await stranger.GetAsync(list);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString);
        }
    }

    [DbFact]
    public async Task Only_a_system_administrator_can_switch_into_a_tenant_by_the_row_of_a_list()
    {
        (_, long second) = await SeedAsync();
        HttpClient tenantAdmin = await SignedInAsync("tenant-admin");

        // a plain administrator posts the switch themselves: nothing changes (SwitchTenant answers a system administrator only)
        await SignIn.PostAsync(tenantAdmin, "/Admin", "/Account/SwitchTenant", new Dictionary<string, string> { ["tenantId"] = second.ToString(), ["returnUrl"] = "/Admin" });
        string admin = await tenantAdmin.GetStringAsync("/Admin");
        Assert.DoesNotContain("Second tenant", admin);
    }
}

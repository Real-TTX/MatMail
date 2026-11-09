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
    private static readonly string[] Lists =
        ["/Platform", "/Platform/Accounts", "/Platform/Mailboxes", "/Platform/Domains", "/Platform/Users", "/Platform/Queue", "/Platform/Transfers", "/Platform/Logs", "/Platform/Administrators", "/Platform/Administrators/Add", "/Platform/New?What=account"];

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

        foreach (string list in Lists.Concat(["/Platform/Tenants", "/Platform/Settings", "/Platform/Backup"]))
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

    [DbFact]
    public async Task The_old_addresses_of_the_pages_that_moved_to_the_platform_lead_there()
    {
        HttpClient admin = await SignedInAsync(PlatformFactory.AdminUser);
        foreach ((string old, string now) in new[]
        {
            ("/Admin/Tenants", "/Platform/Tenants"),
            ("/Admin/Settings/Certificate", "/Platform/Settings/Certificate"),
            ("/Admin/Backup/Plans?page=2", "/Platform/Backup/Plans?page=2"),
        })
        {
            HttpResponseMessage response = await admin.GetAsync(old);
            Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
            Assert.Equal(now, response.Headers.Location!.OriginalString);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Platform/Tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Platform/Settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Platform/Backup")).StatusCode);
    }

    [DbFact]
    public async Task The_events_of_the_installation_are_the_platforms_and_a_tenant_sees_only_its_own()
    {
        (long first, long second) = await SeedAsync();
        string marker = "installation-event-" + Guid.NewGuid().ToString("N")[..8];
        using (IServiceScope scope = site.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            db.ActivityLogs.Add(new ActivityLog { Category = ActivityCategory.System, Level = ActivityLevel.Info, Message = marker + " of the installation" });
            db.ActivityLogs.Add(new ActivityLog { TenantId = second, Category = ActivityCategory.Admin, Level = ActivityLevel.Info, Message = marker + " of the second tenant" });
            db.MailTransfers.Add(new MailTransfer { Direction = TransferDirection.Inbound, Channel = TransferChannel.SmtpServer, Status = TransferStatus.Failed, Sender = "x@y.example", Recipients = marker + "@unknown.example", Subject = marker });
            await db.SaveChangesAsync();
        }

        // the administration of a tenant – even for a system administrator – shows that tenant and nothing of the installation or another tenant
        HttpClient admin = await SignedInAsync(PlatformFactory.AdminUser);
        foreach (string page in new[] { "/Admin/Logs", "/Admin", "/Admin/Transfers" })
        {
            Assert.DoesNotContain(marker, await admin.GetStringAsync(page));
        }

        // the platform shows all of it, and the filter tells the installation from a tenant
        string logs = await admin.GetStringAsync("/Platform/Logs");
        Assert.Contains(marker + " of the installation", logs);
        Assert.Contains(marker + " of the second tenant", logs);
        string installation = await admin.GetStringAsync("/Platform/Logs?TenantId=0");
        Assert.Contains(marker + " of the installation", installation);
        Assert.DoesNotContain(marker + " of the second tenant", installation);
        Assert.DoesNotContain(marker, await admin.GetStringAsync($"/Platform/Logs?TenantId={first}"));
        Assert.Contains(marker + "@unknown.example", await admin.GetStringAsync("/Platform/Transfers"));
    }

    [DbFact]
    public async Task A_platform_administrator_in_another_tenant_sees_it_and_what_they_change_there_is_logged_there()
    {
        (long first, long second) = await SeedAsync();
        HttpClient admin = await SignedInAsync(PlatformFactory.AdminUser);
        string bar = "as a platform administrator";
        Assert.DoesNotContain(bar, await admin.GetStringAsync("/Admin"));     // at home: nothing to say

        HttpResponseMessage switched = await SignIn.PostAsync(admin, "/Platform", "/Account/SwitchTenant", new Dictionary<string, string> { ["tenantId"] = second.ToString(), ["returnUrl"] = "/Admin" });
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Contains(bar, await admin.GetStringAsync("/Admin"));

        // a change in that tenant (here a form with nothing filled in: what counts is that it was sent) is written into its log
        await SignIn.PostAsync(admin, "/Admin/Domains/Edit", "/Admin/Domains/Edit", new Dictionary<string, string> { ["Input.Name"] = "" });
        using (IServiceScope scope = site.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAsSystem();
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Assert.True(await db.ActivityLogs.AnyAsync(l => l.TenantId == second && l.Message.Contains("Platform administrator") && l.Message.Contains("/Admin/Domains/Edit")));
            Assert.False(await db.ActivityLogs.AnyAsync(l => l.TenantId == first && l.Message.Contains("Platform administrator")));
        }

        // and back home
        await SignIn.PostAsync(admin, "/Admin", "/Account/SwitchTenant", new Dictionary<string, string> { ["tenantId"] = first.ToString(), ["returnUrl"] = "/Admin" });
        Assert.DoesNotContain(bar, await admin.GetStringAsync("/Admin"));
    }

    [DbFact]
    public async Task Something_new_from_a_list_of_the_platform_is_made_in_the_tenant_chosen_for_it()
    {
        (long first, long second) = await SeedAsync();
        HttpClient admin = await SignedInAsync(PlatformFactory.AdminUser);

        Assert.Contains("/Platform/New?What=account", await admin.GetStringAsync("/Platform/Accounts"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Platform/New?What=nonsense")).StatusCode);

        HttpResponseMessage response = await SignIn.PostAsync(admin, "/Platform/New?What=account", "/Platform/New?What=account", new Dictionary<string, string> { ["TenantId"] = second.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Admin/Accounts/Edit", response.Headers.Location!.OriginalString);
        string form = await admin.GetStringAsync("/Admin/Accounts/Edit");
        Assert.Contains("Second tenant", form);        // the bar names the tenant the form belongs to
        Assert.Contains("Back to my tenant", form);    // and leads home: the switcher holds only the own tenants

        // a tenant that does not exist is refused, the session stays where it is
        HttpResponseMessage refused = await SignIn.PostAsync(admin, "/Platform/New?What=user", "/Platform/New?What=user", new Dictionary<string, string> { ["TenantId"] = "999999" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);

        await SignIn.PostAsync(admin, "/Admin", "/Account/SwitchTenant", new Dictionary<string, string> { ["tenantId"] = first.ToString(), ["returnUrl"] = "/Admin" });
    }
}

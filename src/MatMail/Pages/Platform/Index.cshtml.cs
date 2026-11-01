using MatMail.Configuration;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Pages.Platform;

/// <summary>The start page of the platform: the server and its backups, and every tenant with what is in it (<see cref="PlatformListService.TenantsAsync"/>).</summary>
public class IndexModel(PlatformListService lists, AppConfig config, CertificateProvider certificates) : PageModel
{
    public IReadOnlyList<PlatformTenantRow> Rows { get; private set; } = Array.Empty<PlatformTenantRow>();

    public PlatformBackupState Backup { get; private set; } = new(null, 0, false);

    public string Hostname => config.Server.Hostname;

    public CertificateInfo? Certificate => certificates.Describe();

    public async Task OnGetAsync()
    {
        Rows = await lists.TenantsAsync(HttpContext.RequestAborted);
        Backup = await lists.BackupStateAsync(HttpContext.RequestAborted);
    }
}

using MatMail.Services;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform.Logs;

/// <summary>The activity log of the whole installation: every tenant and the events that belong to none.</summary>
public class IndexModel(LogListService logs, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : Admin.Logs.IndexModel(logs, currentUser, l)
{
    public override bool AllTenants => true;

    protected override LogScope Scope => LogScope.OfPlatform(TenantId);
}

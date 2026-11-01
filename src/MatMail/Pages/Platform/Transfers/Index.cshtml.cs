using MatMail.Configuration;
using MatMail.Services;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform.Transfers;

/// <summary>The mail transfer log of the whole installation: every tenant and the mail no tenant could be found for.</summary>
public class IndexModel(LogListService logs, CurrentUser currentUser, AppConfig config, IStringLocalizer<SharedResource> l)
    : Admin.Transfers.IndexModel(logs, currentUser, config, l)
{
    public override bool AllTenants => true;

    public override string DetailsPath => "/Platform/Transfers/Details";

    protected override LogScope Scope => LogScope.OfPlatform(TenantId);
}

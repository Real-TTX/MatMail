using MatMail.Services;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform.Transfers;

/// <summary>One line of the mail transfer log of any tenant, or of the installation.</summary>
public class DetailsModel(LogListService logs, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : Admin.Transfers.DetailsModel(logs, currentUser, l)
{
    public override string ListPath => "/Platform/Transfers";

    public override bool ShowQueueLink => false;

    protected override LogScope Scope => LogScope.OfPlatform(null);
}

using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Platform;

public class AccountsModel(PlatformListService lists, IStringLocalizer<SharedResource> l) : PlatformListModel(lists)
{
    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    public PageResult<MailAccount> Paged { get; private set; } = new(Array.Empty<MailAccount>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        Paged = await Lists.AccountsAsync(Query, Enum.TryParse(Role, out MailAccountRole role) ? role : null);
        PageNumber = Paged.PageNumber;
    }

    public string RoleLabel(MailAccountRole role) => role switch
    {
        MailAccountRole.Mail => l["Mail"].Value,
        MailAccountRole.Backup => l["Backup"].Value,
        MailAccountRole.Migration => l["Migration"].Value,
        _ => l["Send only"].Value,
    };
}

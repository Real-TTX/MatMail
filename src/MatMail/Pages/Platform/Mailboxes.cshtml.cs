using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatMail.Pages.Platform;

public class MailboxesModel(PlatformListService lists) : PlatformListModel(lists)
{
    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    public PageResult<PlatformMailboxRow> Paged { get; private set; } = new(Array.Empty<PlatformMailboxRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        Paged = await Lists.MailboxesAsync(Query, Enum.TryParse(Type, out MailboxType type) ? type : null);
        PageNumber = Paged.PageNumber;
    }
}

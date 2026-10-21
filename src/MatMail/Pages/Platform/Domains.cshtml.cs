using MatMail.Services;

namespace MatMail.Pages.Platform;

public class DomainsModel(PlatformListService lists) : PlatformListModel(lists)
{
    public PageResult<PlatformDomainRow> Paged { get; private set; } = new(Array.Empty<PlatformDomainRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        Paged = await Lists.DomainsAsync(Query);
        PageNumber = Paged.PageNumber;
    }
}

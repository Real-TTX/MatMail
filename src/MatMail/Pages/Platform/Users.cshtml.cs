using MatMail.Services;

namespace MatMail.Pages.Platform;

public class UsersModel(PlatformListService lists) : PlatformListModel(lists)
{
    public PageResult<PlatformUserRow> Paged { get; private set; } = new(Array.Empty<PlatformUserRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
        Paged = await Lists.UsersAsync(Query);
        PageNumber = Paged.PageNumber;
    }
}

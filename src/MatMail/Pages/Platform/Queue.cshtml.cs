using MatMail.Services;

namespace MatMail.Pages.Platform;

/// <summary>The outgoing queue of all tenants. Reading only: a row opens the queue entry in the administration of its tenant, where it can be tried again or cancelled.</summary>
public class QueueModel(PlatformListService lists) : PlatformListModel(lists)
{
    public PageResult<PlatformQueueRow> Paged { get; private set; } = new(Array.Empty<PlatformQueueRow>(), 0, 1, 1, Pager.DefaultPageSize);

    public async Task OnGetAsync()
    {
        if (string.IsNullOrEmpty(Request.Query["Sort"]))
        {
            Sort = "created_desc";
        }

        await LoadTenantsAsync();
        Paged = await Lists.QueueAsync(Query);
        PageNumber = Paged.PageNumber;
    }
}

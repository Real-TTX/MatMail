using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Mailboxes;

public class AddressModel(MatMailDbContext db, MailboxService mailboxes, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long MailboxId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public bool CanRegisterDomain => currentUser.Can(Permissions.DomainsManage);
    public IReadOnlyList<SelectListItem> AccountItems { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public string Address { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public bool CanSend { get; set; } = true;
        public long? SendAccountId { get; set; }
        public bool RegisterDomain { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            Input.IsPrimary = !await db.MailboxAliases.AnyAsync(a => a.MailboxId == MailboxId);
            return await MailboxExistsAsync() ? Page() : NotFound();
        }

        MailboxAlias? alias = await db.MailboxAliases.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id && a.MailboxId == MailboxId);
        if (alias is null)
        {
            return NotFound();
        }

        Input = new InputModel { Address = alias.Address, IsPrimary = alias.IsPrimary, CanSend = alias.CanSend, SendAccountId = alias.SendAccountId, RegisterDomain = false };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        Mailbox? mailbox = await db.Mailboxes.FirstOrDefaultAsync(m => m.Id == MailboxId && m.Type != MailboxType.Unassigned);
        if (mailbox is null)
        {
            return NotFound();
        }

        string address = MailAddresses.Normalize(Input.Address);
        if (Input.SendAccountId is long accountId && !AccountItems.Any(a => a.Value == accountId.ToString()))
        {
            ModelState.AddModelError("Input.SendAccountId", l["The account does not exist."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!IsEdit)
        {
            string? error = await mailboxes.AddAddressAsync(
                mailbox, address, Input.IsPrimary, Input.CanSend, Input.SendAccountId, registerDomain: CanRegisterDomain && Input.RegisterDomain);
            if (error is not null)
            {
                ModelState.AddModelError("Input.Address", l[error]);
                return Page();
            }

            this.Notify(l["The address was added."].Value);
            return RedirectToPage("Addresses", new { Id = MailboxId });
        }

        MailboxAlias? alias = await db.MailboxAliases.FirstOrDefaultAsync(a => a.Id == Id && a.MailboxId == MailboxId);
        if (alias is null)
        {
            return NotFound();
        }

        if (address != alias.Address)
        {
            if (!MailAddresses.IsValid(address))
            {
                ModelState.AddModelError("Input.Address", l["The e-mail address is not valid."]);
                return Page();
            }

            string domain = MailAddresses.DomainOf(address);
            Domain? domainRow = await db.Domains.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Name == domain);
            if (domainRow is null && !(CanRegisterDomain && Input.RegisterDomain))
            {
                ModelState.AddModelError("Input.Address", l["The domain of this address is not registered. Add it under Domains first."]);
                return Page();
            }

            if (domainRow is not null && domainRow.TenantId != mailbox.TenantId)
            {
                ModelState.AddModelError("Input.Address", l["The domain of this address belongs to another tenant."]);
                return Page();
            }

            if (await db.MailboxAliases.IgnoreQueryFilters().AnyAsync(a => a.Address == address && a.Id != Id))
            {
                ModelState.AddModelError("Input.Address", l["This address is already in use."]);
                return Page();
            }

            if (domainRow is null)
            {
                db.Domains.Add(new Domain { TenantId = mailbox.TenantId, Name = domain });
            }

            alias.Address = address;
        }

        if (Input.IsPrimary && !MailAddresses.IsCatchAll(alias.Address))
        {
            List<MailboxAlias> others = await db.MailboxAliases.Where(a => a.MailboxId == MailboxId && a.Id != Id && a.IsPrimary).ToListAsync();
            others.ForEach(a => a.IsPrimary = false);
        }

        alias.IsPrimary = Input.IsPrimary && !MailAddresses.IsCatchAll(alias.Address);
        alias.CanSend = Input.CanSend && !MailAddresses.IsCatchAll(alias.Address);
        alias.SendAccountId = Input.SendAccountId;
        await db.SaveChangesAsync();

        this.Notify(l["The address was saved."].Value);
        return RedirectToPage("Addresses", new { Id = MailboxId });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.MailboxAliases.Where(a => a.Id == Id && a.MailboxId == MailboxId).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The address was removed."].Value);
        return RedirectToPage("Addresses", new { Id = MailboxId });
    }

    private Task<bool> MailboxExistsAsync() => db.Mailboxes.AnyAsync(m => m.Id == MailboxId && m.Type != MailboxType.Unassigned);

    private async Task LoadListsAsync()
    {
        AccountItems = await db.MailAccounts.AsNoTracking()
            .Where(a => a.SendHost != null && a.SendHost != string.Empty)
            .OrderBy(a => a.Name)
            .Select(a => new SelectListItem(a.Name + " (" + a.Address + ")", a.Id.ToString()))
            .ToListAsync();
    }
}

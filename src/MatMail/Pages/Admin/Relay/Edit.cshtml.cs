using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Relay;

public class EditModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public IReadOnlyList<SelectListItem> AccountItems { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Network { get; set; } = string.Empty;
        public string? AllowedSenderDomains { get; set; }
        public long? SendAccountId { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        RelayRule? rule = await db.RelayRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == Id);
        if (rule is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = rule.Name,
            Network = rule.Network,
            AllowedSenderDomains = string.Join('\n', rule.AllowedSenderDomains),
            SendAccountId = rule.SendAccountId,
            IsEnabled = rule.IsEnabled,
            Notes = rule.Notes,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        string? network = RelayPolicy.Normalize(Input.Network);
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }

        if (network is null)
        {
            ModelState.AddModelError("Input.Network", l["This is not a valid address or network."]);
        }

        string[] domains = (Input.AllowedSenderDomains ?? string.Empty)
            .Split(new[] { '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant().TrimStart('@')).Distinct().ToArray();
        if (domains.Any(d => !MailAddresses.IsValidDomain(d)))
        {
            ModelState.AddModelError("Input.AllowedSenderDomains", l["One of the domains is not valid."]);
        }

        if (Input.SendAccountId is long accountId && !AccountItems.Any(a => a.Value == accountId.ToString()))
        {
            ModelState.AddModelError("Input.SendAccountId", l["The account does not exist."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        RelayRule rule = IsEdit ? await db.RelayRules.FirstOrDefaultAsync(r => r.Id == Id) ?? new RelayRule() : new RelayRule();
        if (!IsEdit)
        {
            db.RelayRules.Add(rule);
        }

        rule.Name = Input.Name.Trim();
        rule.Network = network!;
        rule.AllowedSenderDomains = domains;
        rule.SendAccountId = Input.SendAccountId;
        rule.IsEnabled = Input.IsEnabled;
        rule.Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();
        await db.SaveChangesAsync();

        this.Notify(l[IsEdit ? "The rule was saved." : "The rule was created."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.RelayRules.Where(r => r.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The rule was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        AccountItems = await db.MailAccounts.AsNoTracking().Where(a => a.SendHost != null && a.SendHost != string.Empty && a.IsEnabled).OrderBy(a => a.Name)
            .Select(a => new SelectListItem(a.Name + " (" + a.Address + ")", a.Id.ToString())).ToListAsync();
    }
}

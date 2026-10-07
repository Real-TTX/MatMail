using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Templates;

public class EditModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>The most a template may hold: pictures are embedded, so it can be large, but not without limit.</summary>
    private const int MaxHtmlLength = 1_000_000;

    /// <summary>What a new template starts with: a header with the name of the company, then the message.</summary>
    private const string StartingPoint =
        "<div style=\"font-family: Arial, sans-serif; font-size: 14px; color: #202124;\">" +
        "<div style=\"border-bottom: 3px solid #1a73e8; padding-bottom: 8px; margin-bottom: 16px; font-size: 18px; font-weight: bold;\">{{Tenant}}</div>" +
        "<div>{{Body}}</div></div>";

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public string Placeholders => "Body," + SignatureService.Placeholders;
    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> UserItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> RuleItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> ScopeItems => new[]
    {
        new SelectListItem(l["The whole tenant"].Value, nameof(AppliesTo.Tenant)),
        new SelectListItem(l["One mailbox"].Value, nameof(AppliesTo.Mailbox)),
        new SelectListItem(l["One user"].Value, nameof(AppliesTo.User)),
    };

    public IReadOnlyList<SelectListItem> ModeItems => new[]
    {
        new SelectListItem(l["Only messages without an HTML version (devices, scripts, simple programs)"].Value, nameof(TemplateMode.PlainTextOnly)),
        new SelectListItem(l["Every message"].Value, nameof(TemplateMode.AllMessages)),
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = nameof(AppliesTo.Tenant);
        public long? MailboxId { get; set; }
        public long? UserId { get; set; }
        public bool ForWebClient { get; set; }
        public bool ForMailPrograms { get; set; } = true;
        public bool ForSmartHost { get; set; } = true;
        public long? RelayRuleId { get; set; }
        public string Mode { get; set; } = nameof(TemplateMode.PlainTextOnly);
        public string Html { get; set; } = StartingPoint;
        public int Priority { get; set; } = 100;
        public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        MailTemplate? template = await db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id);
        if (template is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = template.Name,
            Scope = template.Scope.ToString(),
            MailboxId = template.MailboxId,
            UserId = template.UserId,
            ForWebClient = template.ForWebClient,
            ForMailPrograms = template.ForMailPrograms,
            ForSmartHost = template.ForSmartHost,
            RelayRuleId = template.RelayRuleId,
            Mode = template.Mode.ToString(),
            // The editor shows it in the browser as markup: whatever somebody stored must not run there.
            Html = SignatureService.SanitizeTemplate(template.Html),
            Priority = template.Priority,
            IsActive = template.IsActive,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        Enum.TryParse(Input.Scope, out AppliesTo scope);
        Enum.TryParse(Input.Mode, out TemplateMode mode);

        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.Html))
        {
            ModelState.AddModelError("Input.Html", l["The content is required."]);
        }
        else if (Input.Html.Length > MaxHtmlLength)
        {
            ModelState.AddModelError("Input.Html", l["The template is too large; use smaller pictures."]);
        }
        else if (!TemplateService.HasPlaceForBody(Input.Html))
        {
            ModelState.AddModelError("Input.Html", l["The template must say where the message goes: add the placeholder {{Body}}."]);
        }

        if (scope == AppliesTo.Mailbox && !MailboxItems.Any(m => m.Value == Input.MailboxId?.ToString()))
        {
            ModelState.AddModelError("Input.MailboxId", l["Select a mailbox."]);
        }

        if (scope == AppliesTo.User && !UserItems.Any(u => u.Value == Input.UserId?.ToString()))
        {
            ModelState.AddModelError("Input.UserId", l["Select a user."]);
        }

        if (!Input.ForWebClient && !Input.ForMailPrograms && !Input.ForSmartHost)
        {
            ModelState.AddModelError("Input.ForWebClient", l["Choose where the messages come from."]);
        }

        if (Input.ForSmartHost && Input.RelayRuleId is long ruleId && !RuleItems.Any(r => r.Value == ruleId.ToString()))
        {
            ModelState.AddModelError("Input.RelayRuleId", l["The rule does not exist."]);
        }

        if (Input.Priority is < 0 or > 10_000)
        {
            ModelState.AddModelError("Input.Priority", l["Enter a number between 0 and 10000."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        MailTemplate template = IsEdit ? await db.MailTemplates.FirstOrDefaultAsync(t => t.Id == Id) ?? new MailTemplate() : new MailTemplate();
        if (!IsEdit)
        {
            db.MailTemplates.Add(template);
        }

        template.Name = Input.Name.Trim();
        template.Scope = scope;
        template.MailboxId = scope == AppliesTo.Mailbox ? Input.MailboxId : null;
        template.UserId = scope == AppliesTo.User ? Input.UserId : null;
        template.ForWebClient = Input.ForWebClient;
        template.ForMailPrograms = Input.ForMailPrograms;
        template.ForSmartHost = Input.ForSmartHost;
        template.RelayRuleId = Input.ForSmartHost ? Input.RelayRuleId : null;
        template.Mode = mode;
        template.Html = SignatureService.SanitizeTemplate(Input.Html.Trim());
        template.Priority = Input.Priority;
        template.IsActive = Input.IsActive;
        await db.SaveChangesAsync();

        this.Notify(l[IsEdit ? "The template was saved." : "The template was created."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.MailTemplates.Where(t => t.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The template was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        MailboxItems = await db.Mailboxes.AsNoTracking().Where(m => m.Type != MailboxType.Unassigned).OrderBy(m => m.Name)
            .Select(m => new SelectListItem(m.Name, m.Id.ToString())).ToListAsync();
        UserItems = await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new SelectListItem(u.DisplayName + " (" + u.LoginName + ")", u.Id.ToString())).ToListAsync();
        RuleItems = await db.RelayRules.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToListAsync();
    }
}

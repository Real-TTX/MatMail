using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Signatures;

public class EditModel(MatMailDbContext db, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SelectListItem> UserItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> KindItems => new[]
    {
        new SelectListItem(l["Signature (offered in the mail client)"].Value, nameof(SignatureKind.Signature)),
        new SelectListItem(l["Footer (added by the server to every outgoing message)"].Value, nameof(SignatureKind.Footer)),
    };

    public IReadOnlyList<SelectListItem> ScopeItems => new[]
    {
        new SelectListItem(l["The whole tenant"].Value, nameof(SignatureScope.Tenant)),
        new SelectListItem(l["One mailbox"].Value, nameof(SignatureScope.Mailbox)),
        new SelectListItem(l["One user"].Value, nameof(SignatureScope.User)),
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = nameof(SignatureKind.Signature);
        public string Scope { get; set; } = nameof(SignatureScope.Tenant);
        public long? MailboxId { get; set; }
        public long? UserId { get; set; }
        public string Html { get; set; } = "<p>{{DisplayName}}<br>{{JobTitle}}<br>{{Tenant}}</p>";
        public string? PlainText { get; set; }
        public bool IsDefault { get; set; }
        public bool AddOnServer { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>The most a signature may hold: pictures are embedded, so it can be large, but not without limit.</summary>
    private const int MaxHtmlLength = 1_000_000;

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadListsAsync();
        if (!IsEdit)
        {
            return Page();
        }

        Signature? signature = await db.Signatures.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Id);
        if (signature is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Name = signature.Name,
            Kind = signature.Kind.ToString(),
            Scope = signature.Scope.ToString(),
            MailboxId = signature.MailboxId,
            UserId = signature.UserId,
            // The editor shows it in the browser as markup: whatever somebody stored must not run there.
            Html = SignatureService.SanitizeTemplate(signature.Html),
            PlainText = signature.PlainText,
            IsDefault = signature.IsDefault,
            AddOnServer = signature.AddOnServer,
            IsActive = signature.IsActive,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        Enum.TryParse(Input.Kind, out SignatureKind kind);
        Enum.TryParse(Input.Scope, out SignatureScope scope);

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
            ModelState.AddModelError("Input.Html", l["The signature is too large; use smaller pictures."]);
        }

        if (scope == SignatureScope.Mailbox && !MailboxItems.Any(m => m.Value == Input.MailboxId?.ToString()))
        {
            ModelState.AddModelError("Input.MailboxId", l["Select a mailbox."]);
        }

        if (scope == SignatureScope.User && !UserItems.Any(u => u.Value == Input.UserId?.ToString()))
        {
            ModelState.AddModelError("Input.UserId", l["Select a user."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Signature signature = IsEdit ? await db.Signatures.FirstOrDefaultAsync(s => s.Id == Id) ?? new Signature() : new Signature();
        if (!IsEdit)
        {
            db.Signatures.Add(signature);
        }

        signature.Name = Input.Name.Trim();
        signature.Kind = kind;
        signature.Scope = scope;
        signature.MailboxId = scope == SignatureScope.Mailbox ? Input.MailboxId : null;
        signature.UserId = scope == SignatureScope.User ? Input.UserId : null;
        signature.Html = SignatureService.SanitizeTemplate(Input.Html.Trim());
        signature.PlainText = string.IsNullOrWhiteSpace(Input.PlainText) ? null : Input.PlainText.Trim();
        signature.IsActive = Input.IsActive;
        signature.IsDefault = kind == SignatureKind.Signature && Input.IsDefault;
        signature.AddOnServer = kind == SignatureKind.Signature && Input.AddOnServer;

        // One default per target: the previous default of the same scope loses it.
        if (signature.IsDefault)
        {
            long? mailboxId = signature.MailboxId;
            long? userId = signature.UserId;
            long ownId = signature.Id;
            List<Signature> others = await db.Signatures
                .Where(s => s.Id != ownId && s.IsDefault && s.Scope == scope && s.MailboxId == mailboxId && s.UserId == userId)
                .ToListAsync();
            others.ForEach(s => s.IsDefault = false);
        }

        await db.SaveChangesAsync();
        this.Notify(l[IsEdit ? "The signature was saved." : "The signature was created."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.Signatures.Where(s => s.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The signature was deleted."].Value);
        return RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        MailboxItems = await db.Mailboxes.AsNoTracking().Where(m => m.Type != MailboxType.Unassigned).OrderBy(m => m.Name)
            .Select(m => new SelectListItem(m.Name, m.Id.ToString())).ToListAsync();
        UserItems = await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new SelectListItem(u.DisplayName + " (" + u.LoginName + ")", u.Id.ToString())).ToListAsync();
    }
}

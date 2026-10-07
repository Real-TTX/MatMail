using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Tenants;

public class EditModel(MatMailDbContext db, TenantService tenants, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public bool IsCurrent => Id == currentUser.TenantId;

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsEdit)
        {
            return Page();
        }

        Tenant? tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == Id);
        if (tenant is null)
        {
            return NotFound();
        }

        Input = new InputModel { Name = tenant.Name, Description = tenant.Description, IsActive = tenant.IsActive };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        string name = Input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
            return Page();
        }

        if (!IsEdit)
        {
            (Tenant? created, string? createError) = await tenants.CreateAsync(name, Input.Description);
            if (created is null)
            {
                ModelState.AddModelError("Input.Name", l[createError!]);
                return Page();
            }

            if (!Input.IsActive)
            {
                Tenant row = await db.Tenants.FirstAsync(t => t.Id == created.Id);
                row.IsActive = false;
                await db.SaveChangesAsync();
            }

            this.Notify(l["The tenant was created."].Value);
            return RedirectToPage("Index");
        }

        Tenant? tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == Id);
        if (tenant is null)
        {
            return NotFound();
        }

        if (await db.Tenants.AnyAsync(t => t.Id != Id && t.Name.ToLower() == name.ToLower()))
        {
            ModelState.AddModelError("Input.Name", l["A tenant with that name already exists."]);
            return Page();
        }

        if (!Input.IsActive && IsCurrent)
        {
            ModelState.AddModelError("Input.IsActive", l["You cannot deactivate the tenant you are working in."]);
            return Page();
        }

        tenant.Name = name;
        tenant.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        tenant.IsActive = Input.IsActive;
        await db.SaveChangesAsync();

        this.Notify(l["The tenant was saved."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        string? error = await tenants.DeleteAsync(Id);
        if (error is not null)
        {
            this.Notify(l[error].Value, NoticeKind.Danger);
            return RedirectToPage(new { Id });
        }

        this.Notify(l["The tenant was deleted."].Value);
        return RedirectToPage("Index");
    }
}

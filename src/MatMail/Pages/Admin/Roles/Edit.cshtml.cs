using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Roles;

public class EditModel(MatMailDbContext db, SessionCache cache, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public bool IsBuiltIn { get; private set; }

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string[] Permissions { get; set; } = Array.Empty<string>();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsEdit)
        {
            Input.Permissions = Services.Permissions.UserDefaults.ToArray();
            return Page();
        }

        Role? role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == Id);
        if (role is null)
        {
            return NotFound();
        }

        IsBuiltIn = role.IsBuiltIn;
        Input = new InputModel { Name = role.Name, Description = role.Description, Permissions = role.Permissions };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        string name = Input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }
        else if (await db.Roles.AnyAsync(r => r.Id != Id && r.Name.ToLower() == name.ToLower()))
        {
            ModelState.AddModelError("Input.Name", l["A role with that name already exists."]);
        }

        if (!ModelState.IsValid)
        {
            IsBuiltIn = IsEdit && await db.Roles.AnyAsync(r => r.Id == Id && r.IsBuiltIn);
            return Page();
        }

        string[] permissions = (Input.Permissions ?? Array.Empty<string>()).Where(Services.Permissions.IsKnown).Distinct().ToArray();
        Role role;
        if (IsEdit)
        {
            Role? existing = await db.Roles.FirstOrDefaultAsync(r => r.Id == Id);
            if (existing is null)
            {
                return NotFound();
            }

            role = existing;
        }
        else
        {
            role = new Role();
            db.Roles.Add(role);
        }

        role.Name = name;
        role.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        role.Permissions = permissions;
        await db.SaveChangesAsync();
        cache.Clear();

        this.Notify(l[IsEdit ? "The role was saved." : "The role was created."].Value);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        Role? role = await db.Roles.FirstOrDefaultAsync(r => r.Id == Id);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsBuiltIn)
        {
            this.Notify(l["Built-in roles cannot be deleted."].Value, NoticeKind.Danger);
            return RedirectToPage(new { Id });
        }

        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == Id))
        {
            this.Notify(l["This role is still assigned to users."].Value, NoticeKind.Danger);
            return RedirectToPage(new { Id });
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        cache.Clear();
        this.Notify(l["The role was deleted."].Value);
        return RedirectToPage("Index");
    }
}

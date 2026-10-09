using System.Text.RegularExpressions;
using MatMail.Data;
using MatMail.Directories;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Directories;

public partial class EditModel(
    MatMailDbContext db, DirectoryService directories, DirectoryProvisioner provisioner, SecretProtector secrets, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public DirectoryConnection? Connection { get; private set; }

    /// <summary>The result of "Test connection": what was found, shown below the form.</summary>
    public DirectoryCheck? Check { get; private set; }

    /// <summary>How many users sign in through this directory.</summary>
    public int LinkedUsers { get; private set; }

    public bool HasStoredPassword => !string.IsNullOrEmpty(Connection?.BindPasswordProtected);
    public IReadOnlyList<SelectListItem> RoleItems { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> PresetItems => new[]
    {
        new SelectListItem(l["Fill in the usual values for …"].Value, string.Empty),
        new SelectListItem("Active Directory", "ad"),
        new SelectListItem("OpenLDAP / 389 Directory Server", "openldap"),
        new SelectListItem("FreeIPA / Red Hat IdM", "freeipa"),
    };

    public IReadOnlyList<SelectListItem> SecurityItems => new[]
    {
        new SelectListItem(l["STARTTLS (port 389, switched to encryption before anything is sent)"].Value, nameof(DirectorySecurity.StartTls)),
        new SelectListItem(l["LDAPS (port 636, encrypted from the start)"].Value, nameof(DirectorySecurity.Ldaps)),
        new SelectListItem(l["None (not encrypted: passwords travel in the clear)"].Value, nameof(DirectorySecurity.None)),
    };

    public IReadOnlyList<SelectListItem> LookupItems => new[]
    {
        new SelectListItem(l["From the person (memberOf: Active Directory, FreeIPA, OpenLDAP with the memberOf overlay)"].Value, nameof(GroupLookup.UserMemberOf)),
        new SelectListItem(l["From the group (its member list: OpenLDAP without memberOf)"].Value, nameof(GroupLookup.GroupMembers)),
    };

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public string? Preset { get; set; }

        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 389;
        public string Security { get; set; } = nameof(DirectorySecurity.StartTls);
        public bool AllowInvalidCertificate { get; set; }
        public string? BindDn { get; set; }
        public string? BindPassword { get; set; }

        public string BaseDn { get; set; } = string.Empty;
        public string UserFilter { get; set; } = "(objectClass=person)";
        public string LoginAttribute { get; set; } = "uid";

        public string DisplayNameAttribute { get; set; } = "cn";
        public string? EmailAttribute { get; set; } = "mail";
        public string? FirstNameAttribute { get; set; } = "givenName";
        public string? LastNameAttribute { get; set; } = "sn";
        public string? JobTitleAttribute { get; set; } = "title";
        public string? PhoneAttribute { get; set; } = "telephoneNumber";
        public string? MobileAttribute { get; set; } = "mobile";
        public string? DepartmentAttribute { get; set; } = "department";

        public string? AllowedGroupDn { get; set; }
        public string GroupLookup { get; set; } = nameof(Data.GroupLookup.UserMemberOf);
        public bool NestedGroups { get; set; }

        public bool CreateUsersOnSignIn { get; set; } = true;
        public long[] DefaultRoleIds { get; set; } = Array.Empty<long>();
        public bool CreateMailbox { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (!IsEdit)
        {
            return Page();
        }

        if (Connection is null)
        {
            return NotFound();
        }

        Input = ToInput(Connection);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (IsEdit && Connection is null)
        {
            return NotFound();
        }

        await ValidateAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        DirectoryConnection dir = Connection is null ? new DirectoryConnection() : await db.DirectoryConnections.FirstAsync(d => d.Id == Id);
        Apply(dir, Connection);
        if (Connection is null)
        {
            db.DirectoryConnections.Add(dir);
        }

        await db.SaveChangesAsync();

        // saved: say at once whether it works
        DirectoryCheck check = await directories.TestAsync(dir, string.IsNullOrEmpty(Input.BindPassword) ? null : Input.BindPassword, HttpContext.RequestAborted);
        dir.LastCheckDate = DateTime.UtcNow;
        dir.LastCheckOk = check.Ok;
        dir.LastCheckMessage = check.Message;
        await db.SaveChangesAsync();

        this.Notify(
            check.Ok ? l["The directory was saved. {0}"].Value.Replace("{0}", check.Message) : l["The directory was saved, but it cannot be used yet: {0}"].Value.Replace("{0}", check.Message),
            check.Ok ? NoticeKind.Ok : NoticeKind.Warn);
        return RedirectToPage("Index");
    }

    /// <summary>Tries the connection with what is in the form (nothing is saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        await LoadAsync();
        await ValidateAsync(forTest: true);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new DirectoryConnection { TenantId = Connection?.TenantId ?? 0 };
        Apply(probe, Connection);
        Check = await directories.TestAsync(probe, string.IsNullOrEmpty(Input.BindPassword) ? null : Input.BindPassword, HttpContext.RequestAborted);
        this.NotifyNow(Check.Ok, Check.Message);
        return Page();
    }

    /// <summary>Compares the users of this directory with it now.</summary>
    public async Task<IActionResult> OnPostCompareAsync()
    {
        await LoadAsync();
        if (Connection is null)
        {
            return NotFound();
        }

        Input = ToInput(Connection);
        DirectoryConnection dir = await db.DirectoryConnections.FirstAsync(d => d.Id == Id);
        DirectorySyncResult result = await provisioner.SyncAsync(dir, HttpContext.RequestAborted);
        await LoadAsync();
        this.NotifyNow(result.Ok, result.Message + (result.Notes.Count == 0 ? string.Empty : " " + string.Join(" ", result.Notes)));
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        int deleted = await db.DirectoryConnections.Where(d => d.Id == Id).ExecuteDeleteAsync();
        if (deleted == 0)
        {
            return NotFound();
        }

        this.Notify(l["The directory was deleted."].Value);
        return RedirectToPage("Index");
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        RoleItems = await db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToListAsync();
        if (IsEdit)
        {
            Connection = await db.DirectoryConnections.AsNoTracking().FirstOrDefaultAsync(d => d.Id == Id);
            LinkedUsers = await db.Users.CountAsync(u => u.DirectoryId == Id);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.;-]{0,99}$")]
    private static partial Regex AttributeName();

    private async Task ValidateAsync(bool forTest = false)
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }
        else if (!forTest && await db.DirectoryConnections.AnyAsync(d => d.Name == Input.Name.Trim() && d.Id != Id))
        {
            ModelState.AddModelError("Input.Name", l["There is a directory of this name already."]);
        }

        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", l["The server is required."]);
        }

        if (Input.Port is < 1 or > 65535)
        {
            ModelState.AddModelError("Input.Port", l["The port must be between 1 and 65535."]);
        }

        if (string.IsNullOrWhiteSpace(Input.BaseDn))
        {
            ModelState.AddModelError("Input.BaseDn", l["The base is required."]);
        }

        if (string.IsNullOrWhiteSpace(Input.UserFilter) || !LdapFilter.IsBalanced(LdapFilter.Normalize(Input.UserFilter)))
        {
            ModelState.AddModelError("Input.UserFilter", l["The filter is not valid: every opening bracket needs its closing one, e.g. (objectClass=person)."]);
        }

        if (!string.IsNullOrWhiteSpace(Input.BindDn) && !forTest && string.IsNullOrEmpty(Input.BindPassword) && !HasStoredPassword)
        {
            ModelState.AddModelError("Input.BindPassword", l["The password of the account is required."]);
        }

        CheckAttribute(nameof(Input.LoginAttribute), Input.LoginAttribute, required: true);
        CheckAttribute(nameof(Input.DisplayNameAttribute), Input.DisplayNameAttribute, required: true);
        CheckAttribute(nameof(Input.EmailAttribute), Input.EmailAttribute, required: false);
        CheckAttribute(nameof(Input.FirstNameAttribute), Input.FirstNameAttribute, required: false);
        CheckAttribute(nameof(Input.LastNameAttribute), Input.LastNameAttribute, required: false);
        CheckAttribute(nameof(Input.JobTitleAttribute), Input.JobTitleAttribute, required: false);
        CheckAttribute(nameof(Input.PhoneAttribute), Input.PhoneAttribute, required: false);
        CheckAttribute(nameof(Input.MobileAttribute), Input.MobileAttribute, required: false);
        CheckAttribute(nameof(Input.DepartmentAttribute), Input.DepartmentAttribute, required: false);

        long[] known = RoleItems.Select(r => long.Parse(r.Value)).ToArray();
        if ((Input.DefaultRoleIds ?? Array.Empty<long>()).Any(id => !known.Contains(id)))
        {
            ModelState.AddModelError("Input.DefaultRoleIds", l["The role does not exist."]);
        }
    }

    private void CheckAttribute(string field, string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                ModelState.AddModelError("Input." + field, l["The attribute is required."]);
            }

            return;
        }

        if (!AttributeName().IsMatch(value.Trim()))
        {
            ModelState.AddModelError("Input." + field, l["Only the name of an attribute, e.g. mail."]);
        }
    }

    /// <summary>Copies the form into the entity. An empty password field keeps the stored (encrypted) password.</summary>
    private void Apply(DirectoryConnection dir, DirectoryConnection? passwordSource)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        Enum.TryParse(Input.Security, out DirectorySecurity security);
        Enum.TryParse(Input.GroupLookup, out GroupLookup lookup);

        dir.Name = Input.Name.Trim();
        dir.IsActive = Input.IsActive;
        dir.Host = Input.Host.Trim();
        dir.Port = Input.Port;
        dir.Security = security;
        dir.AllowInvalidCertificate = Input.AllowInvalidCertificate && security != DirectorySecurity.None;
        dir.BindDn = Clean(Input.BindDn);
        dir.BindPasswordProtected = dir.BindDn is null
            ? null
            : string.IsNullOrEmpty(Input.BindPassword) ? passwordSource?.BindPasswordProtected : secrets.Protect(Input.BindPassword);

        dir.BaseDn = Input.BaseDn.Trim();
        dir.UserFilter = LdapFilter.Normalize(Input.UserFilter);
        dir.LoginAttribute = Input.LoginAttribute.Trim();
        dir.DisplayNameAttribute = Input.DisplayNameAttribute.Trim();
        dir.EmailAttribute = Clean(Input.EmailAttribute);
        dir.FirstNameAttribute = Clean(Input.FirstNameAttribute);
        dir.LastNameAttribute = Clean(Input.LastNameAttribute);
        dir.JobTitleAttribute = Clean(Input.JobTitleAttribute);
        dir.PhoneAttribute = Clean(Input.PhoneAttribute);
        dir.MobileAttribute = Clean(Input.MobileAttribute);
        dir.DepartmentAttribute = Clean(Input.DepartmentAttribute);

        dir.AllowedGroupDn = Clean(Input.AllowedGroupDn);
        dir.GroupLookup = lookup;
        dir.NestedGroups = Input.NestedGroups && lookup == GroupLookup.UserMemberOf;

        dir.CreateUsersOnSignIn = Input.CreateUsersOnSignIn;
        dir.DefaultRoleIds = (Input.DefaultRoleIds ?? Array.Empty<long>()).Distinct().ToArray();
        dir.CreateMailbox = Input.CreateMailbox;
    }

    private static InputModel ToInput(DirectoryConnection d) => new()
    {
        Name = d.Name,
        IsActive = d.IsActive,
        Host = d.Host,
        Port = d.Port,
        Security = d.Security.ToString(),
        AllowInvalidCertificate = d.AllowInvalidCertificate,
        BindDn = d.BindDn,
        BaseDn = d.BaseDn,
        UserFilter = d.UserFilter,
        LoginAttribute = d.LoginAttribute,
        DisplayNameAttribute = d.DisplayNameAttribute,
        EmailAttribute = d.EmailAttribute,
        FirstNameAttribute = d.FirstNameAttribute,
        LastNameAttribute = d.LastNameAttribute,
        JobTitleAttribute = d.JobTitleAttribute,
        PhoneAttribute = d.PhoneAttribute,
        MobileAttribute = d.MobileAttribute,
        DepartmentAttribute = d.DepartmentAttribute,
        AllowedGroupDn = d.AllowedGroupDn,
        GroupLookup = d.GroupLookup.ToString(),
        NestedGroups = d.NestedGroups,
        CreateUsersOnSignIn = d.CreateUsersOnSignIn,
        DefaultRoleIds = d.DefaultRoleIds,
        CreateMailbox = d.CreateMailbox,
    };
}

using MatMail.Data;
using MatMail.Directories;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Directories;

/// <summary>Chooses people of a directory and makes users of them, so that they exist (and can be given a mailbox and rights) before they sign in the first time.</summary>
public class ImportModel(MatMailDbContext db, DirectoryService directories, DirectoryProvisioner provisioner, IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>How many people one page shows (a directory can have many thousands: the search narrows it down).</summary>
    public const int Limit = 200;

    /// <summary>The most people one import takes.</summary>
    public const int MaxImport = 500;

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public DirectoryConnection? Connection { get; private set; }
    public IReadOnlyList<PersonRow> People { get; private set; } = Array.Empty<PersonRow>();
    public IReadOnlyList<SelectListItem> RoleItems { get; private set; } = Array.Empty<SelectListItem>();
    public bool Truncated { get; private set; }

    [BindProperty]
    public ImportInput Input { get; set; } = new();

    public enum PersonState
    {
        /// <summary>There is no user of this name yet.</summary>
        New,

        /// <summary>A user was made from this person (or they signed in already).</summary>
        Imported,

        /// <summary>A user with this login name exists that has a password of its own.</summary>
        Local,

        /// <summary>The login name belongs to a user of another directory or tenant.</summary>
        Taken,
    }

    public sealed record PersonRow(DirectoryUser Person, PersonState State);

    public class ImportInput
    {
        public string[] Selected { get; set; } = Array.Empty<string>();
        public long[] RoleIds { get; set; } = Array.Empty<long>();
        public bool CreateMailbox { get; set; } = true;
        public bool SwitchExisting { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Input.RoleIds = Connection!.DefaultRoleIds;
        Input.CreateMailbox = Connection.CreateMailbox;
        await SearchAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        string[] logins = (Input.Selected ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxImport).ToArray();
        if (logins.Length == 0)
        {
            this.Notify(l["Nobody was chosen."].Value, NoticeKind.Warn);
            return RedirectToPage(new { Id, Search });
        }

        DirectoryConnection dir = await db.DirectoryConnections.FirstAsync(d => d.Id == Id);
        long[] roles = (Input.RoleIds ?? Array.Empty<long>()).Distinct().ToArray();
        int created = 0, switched = 0, skipped = 0;
        var problems = new List<string>();
        try
        {
            foreach (string login in logins)
            {
                DirectoryLookup found = await directories.FindUserAsync(dir, login, HttpContext.RequestAborted);
                if (found.User is not { } person)
                {
                    skipped++;
                    problems.Add(string.Format(l["“{0}” is not in the directory (any more) or not in the group that may sign in."].Value, login));
                    continue;
                }

                User? known = await provisioner.FindLinkedAsync(dir, person, HttpContext.RequestAborted)
                              ?? await db.Users.FirstOrDefaultAsync(u => u.LoginName == person.Login);
                if (known is null)
                {
                    (User? user, string? error) = await provisioner.CreateUserAsync(dir, person, roles, Input.CreateMailbox, HttpContext.RequestAborted);
                    if (user is null)
                    {
                        skipped++;
                        problems.Add($"{person.Login}: {error}");
                        continue;
                    }

                    created++;
                }
                else if (known.DirectoryId == dir.Id)
                {
                    skipped++;   // there already
                }
                else if (known.DirectoryId is not null)
                {
                    skipped++;
                    problems.Add(string.Format(l["“{0}” signs in through another directory."].Value, person.Login));
                }
                else if (Input.SwitchExisting)
                {
                    string? error = await provisioner.LinkAsync(dir, person, known, HttpContext.RequestAborted);
                    if (error is not null)
                    {
                        skipped++;
                        problems.Add(error);
                        continue;
                    }

                    switched++;
                }
                else
                {
                    skipped++;
                    problems.Add(string.Format(l["“{0}” is a user with a password of their own already."].Value, person.Login));
                }
            }
        }
        catch (DirectoryException ex)
        {
            this.Notify(ex.Message, NoticeKind.Danger);
            return RedirectToPage(new { Id, Search });
        }

        string summary = string.Format(l["{0} users made, {1} switched to the directory, {2} left out."].Value, created, switched, skipped);
        if (problems.Count > 0)
        {
            summary += " " + string.Join(" ", problems.Take(5)) + (problems.Count > 5 ? " …" : string.Empty);
        }

        this.Notify(summary, created + switched > 0 ? (skipped > 0 ? NoticeKind.Warn : NoticeKind.Ok) : NoticeKind.Warn);
        return RedirectToPage(new { Id, Search });
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async Task<bool> LoadAsync()
    {
        Connection = await db.DirectoryConnections.AsNoTracking().FirstOrDefaultAsync(d => d.Id == Id);
        if (Connection is null)
        {
            return false;
        }

        RoleItems = await db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToListAsync();
        return true;
    }

    /// <summary>The people the directory has (or the search finds), each with what MatMail knows of them.</summary>
    private async Task SearchAsync()
    {
        try
        {
            IReadOnlyList<DirectoryUser> found = await directories.SearchUsersAsync(Connection!, Search, Limit + 1, HttpContext.RequestAborted);
            Truncated = found.Count > Limit;

            DirectoryUser[] page = found.Take(Limit).ToArray();
            string[] logins = page.Select(p => p.Login).ToArray();
            var users = await db.Users.AsNoTracking().Where(u => logins.Contains(u.LoginName))
                .Select(u => new { u.LoginName, u.DirectoryId, u.DirectoryUid, u.DirectoryDn }).ToListAsync();
            var linked = await db.Users.AsNoTracking().Where(u => u.DirectoryId == Id).Select(u => new { u.DirectoryUid, u.DirectoryDn }).ToListAsync();

            People = page.Select(p =>
            {
                bool imported = linked.Any(u => (!string.IsNullOrEmpty(p.Uid) && u.DirectoryUid == p.Uid) || LdapFilter.SameDn(u.DirectoryDn, p.Dn));
                var user = users.FirstOrDefault(u => u.LoginName == p.Login);
                PersonState state = imported || user?.DirectoryId == Id ? PersonState.Imported
                    : user is null ? PersonState.New
                    : user.DirectoryId is null ? PersonState.Local : PersonState.Taken;
                return new PersonRow(p, state);
            }).ToList();
        }
        catch (DirectoryException ex)
        {
            this.NotifyNow(ex.Message, NoticeKind.Danger);
        }
    }
}

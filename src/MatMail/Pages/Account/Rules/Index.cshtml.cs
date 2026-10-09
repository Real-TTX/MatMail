using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account.Rules;

/// <summary>
/// The rules of a mailbox: what happens to mail that arrives. Everybody manages the rules of their own mailbox and of the mailboxes
/// they were given full control of; administrators do not get at them by their role (a rule can forward mail, so it is the same as reading it).
/// </summary>
public class IndexModel(MatMailDbContext db, MailAccessService access, IStringLocalizer<SharedResource> l) : PageModel
{
    public const int MaxRulesPerMailbox = 100;

    [BindProperty(SupportsGet = true)]
    public long? MailboxId { get; set; }

    public IReadOnlyList<SelectListItem> MailboxItems { get; private set; } = Array.Empty<SelectListItem>();
    public string? MailboxName { get; private set; }
    public IReadOnlyList<RuleRow> Rules { get; private set; } = Array.Empty<RuleRow>();
    public bool CanAdd => Rules.Count < MaxRulesPerMailbox;

    public sealed record RuleRow(long Id, string Name, bool IsActive, bool StopProcessing, IReadOnlyList<string> When, IReadOnlyList<string> Then, bool MatchAny, long MatchCount, DateTime? LastMatchDate);

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    /// <summary>Moves a rule one place up or down (rules are applied from the top).</summary>
    public async Task<IActionResult> OnPostMoveAsync(long id, string direction)
    {
        MailRule? rule = await FindManagedRuleAsync(id);
        if (rule is null)
        {
            return NotFound();
        }

        List<MailRule> all = await db.MailRules.Where(r => r.MailboxId == rule.MailboxId).OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].Position = i;
        }

        int index = all.FindIndex(r => r.Id == id);
        int other = direction == "up" ? index - 1 : index + 1;
        if (other >= 0 && other < all.Count)
        {
            (all[index].Position, all[other].Position) = (all[other].Position, all[index].Position);
        }

        await db.SaveChangesAsync();
        return RedirectToPage(new { MailboxId = rule.MailboxId });
    }

    /// <summary>Switches a rule on or off without losing it.</summary>
    public async Task<IActionResult> OnPostToggleAsync(long id)
    {
        MailRule? rule = await FindManagedRuleAsync(id);
        if (rule is null)
        {
            return NotFound();
        }

        rule.IsActive = !rule.IsActive;
        await db.SaveChangesAsync();
        this.Notify(l[rule.IsActive ? "The rule is on." : "The rule is off."].Value);
        return RedirectToPage(new { MailboxId = rule.MailboxId });
    }

    private async Task<MailRule?> FindManagedRuleAsync(long id)
    {
        MailRule? rule = await db.MailRules.FirstOrDefaultAsync(r => r.Id == id);
        MailUser? user = access.GetCurrentUser();
        if (rule is null || user is null)
        {
            return null;
        }

        // No access at all is null, and null compares as "not smaller": the level has to be there and be enough.
        MailboxAccess? level = await access.GetAccessAsync(user, rule.MailboxId);
        return level >= MailboxAccess.Manage ? rule : null;
    }

    private async Task LoadAsync()
    {
        MailUser? user = access.GetCurrentUser();
        IReadOnlyList<AccessibleMailbox> mailboxes = user is null
            ? Array.Empty<AccessibleMailbox>()
            : (await access.GetMailboxesAsync(user)).Where(m => m.Access >= MailboxAccess.Manage && m.Mailbox.Type != MailboxType.Unassigned).ToList();
        MailboxItems = mailboxes.Select(m => new SelectListItem(m.IsOwn ? l["My mailbox"].Value : m.Mailbox.Name, m.Mailbox.Id.ToString())).ToList();

        AccessibleMailbox? chosen = mailboxes.FirstOrDefault(m => m.Mailbox.Id == MailboxId) ?? mailboxes.FirstOrDefault();
        if (chosen is null)
        {
            return;
        }

        MailboxId = chosen.Mailbox.Id;
        MailboxName = chosen.IsOwn ? l["My mailbox"].Value : chosen.Mailbox.Name;

        var labels = new RuleLabels(l);
        IReadOnlyDictionary<long, string> folders = labels.FolderPaths(await db.MailFolders.AsNoTracking().Where(f => f.MailboxId == chosen.Mailbox.Id).ToListAsync());
        List<MailRule> rules = await db.MailRules.AsNoTracking().Include(r => r.Conditions).Include(r => r.Actions)
            .Where(r => r.MailboxId == chosen.Mailbox.Id).OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync();
        Rules = rules.Select(r => new RuleRow(
            r.Id, r.Name, r.IsActive, r.StopProcessing,
            r.Conditions.OrderBy(c => c.Id).Select(labels.Describe).ToList(),
            r.Actions.OrderBy(a => a.Id).Select(a => labels.Describe(a, folders)).ToList(),
            r.Match == RuleMatch.Any, r.MatchCount, r.LastMatchDate)).ToList();
    }
}

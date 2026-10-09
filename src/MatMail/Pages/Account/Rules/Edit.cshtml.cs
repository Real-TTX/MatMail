using System.Globalization;
using System.Text.Json;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account.Rules;

public class EditModel(MatMailDbContext db, MailAccessService access, IStringLocalizer<SharedResource> l) : PageModel
{
    private const int MaxConditions = 20;
    private const int MaxActions = 10;

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The mailbox the rule belongs to (given when a rule is created; a rule that exists tells it itself).</summary>
    [BindProperty(SupportsGet = true)]
    public long MailboxId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Id != 0;
    public string MailboxName { get; private set; } = string.Empty;
    public RuleLabels Labels { get; } = new(l);
    public IReadOnlyList<SelectListItem> FolderItems { get; private set; } = Array.Empty<SelectListItem>();

    public class InputModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public RuleMatch Match { get; set; } = RuleMatch.All;
        public bool StopProcessing { get; set; }
        public List<ConditionInput> Conditions { get; set; } = new();
        public List<ActionInput> Actions { get; set; } = new();
    }

    public class ConditionInput
    {
        public RuleField Field { get; set; } = RuleField.From;
        public RuleOperator Operator { get; set; } = RuleOperator.Contains;
        public string? Value { get; set; }
        public string? HeaderName { get; set; }
    }

    public class ActionInput
    {
        public RuleActionType Type { get; set; } = RuleActionType.MoveToFolder;
        public long? FolderId { get; set; }
        public string? Value { get; set; }
    }

    /// <summary>What the script needs to change the operators when the field of a condition changes: field → [[operator, text], …].</summary>
    public string OperatorConfigJson => JsonSerializer.Serialize(
        Enum.GetValues<RuleField>().ToDictionary(f => f.ToString(), f => RuleLabels.OperatorsFor(f).Select(o => new[] { o.ToString(), Labels.Operator(o) }).ToArray()));

    public async Task<IActionResult> OnGetAsync()
    {
        MailRule? rule = null;
        if (IsEdit)
        {
            rule = await db.MailRules.AsNoTracking().Include(r => r.Conditions).Include(r => r.Actions).FirstOrDefaultAsync(r => r.Id == Id);
            if (rule is null)
            {
                return NotFound();
            }

            MailboxId = rule.MailboxId;
        }

        if (!await LoadMailboxAsync())
        {
            return NotFound();
        }

        if (rule is null)
        {
            Input = new InputModel
            {
                Conditions = { new ConditionInput() },
                Actions = { new ActionInput { FolderId = FolderItems.Select(f => (long?)long.Parse(f.Value!)).FirstOrDefault() } },
            };
            return Page();
        }

        Input = new InputModel
        {
            Name = rule.Name,
            IsActive = rule.IsActive,
            Match = rule.Match,
            StopProcessing = rule.StopProcessing,
            Conditions = rule.Conditions.OrderBy(c => c.Id)
                .Select(c => new ConditionInput { Field = c.Field, Operator = c.Operator, Value = c.Value, HeaderName = c.HeaderName }).ToList(),
            Actions = rule.Actions.OrderBy(a => a.Id)
                .Select(a => new ActionInput { Type = a.Type, FolderId = a.FolderId, Value = a.Value }).ToList(),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        MailRule? rule = null;
        if (IsEdit)
        {
            rule = await db.MailRules.Include(r => r.Conditions).Include(r => r.Actions).FirstOrDefaultAsync(r => r.Id == Id);
            if (rule is null)
            {
                return NotFound();
            }

            MailboxId = rule.MailboxId;
        }

        if (!await LoadMailboxAsync())
        {
            return NotFound();
        }

        Input.Conditions ??= new List<ConditionInput>();
        Input.Actions ??= new List<ActionInput>();
        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (rule is null)
        {
            int count = await db.MailRules.CountAsync(r => r.MailboxId == MailboxId);
            if (count >= IndexModel.MaxRulesPerMailbox)
            {
                ModelState.AddModelError(string.Empty, l["A mailbox can have {0} rules at most.", IndexModel.MaxRulesPerMailbox]);
                return Page();
            }

            int last = await db.MailRules.Where(r => r.MailboxId == MailboxId).Select(r => (int?)r.Position).MaxAsync() ?? -1;
            long tenantId = await db.Mailboxes.Where(m => m.Id == MailboxId).Select(m => m.TenantId).FirstAsync();
            rule = new MailRule { MailboxId = MailboxId, TenantId = tenantId, Position = last + 1 };
            db.MailRules.Add(rule);
        }
        else
        {
            db.MailRuleConditions.RemoveRange(rule.Conditions);
            db.MailRuleActions.RemoveRange(rule.Actions);
            rule.Conditions = new List<MailRuleCondition>();
            rule.Actions = new List<MailRuleAction>();
        }

        rule.Name = Input.Name.Trim();
        rule.IsActive = Input.IsActive;
        rule.Match = Input.Match;
        rule.StopProcessing = Input.StopProcessing;
        foreach (ConditionInput c in Input.Conditions)
        {
            rule.Conditions.Add(ToEntity(c, rule.TenantId));
        }

        foreach (ActionInput a in Input.Actions)
        {
            rule.Actions.Add(ToEntity(a, rule.TenantId));
        }

        await db.SaveChangesAsync();
        this.Notify(l[IsEdit ? "The rule was saved." : "The rule was created."].Value);
        return RedirectToPage("Index", new { MailboxId });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        MailRule? rule = await db.MailRules.FirstOrDefaultAsync(r => r.Id == Id);
        if (rule is null)
        {
            return NotFound();
        }

        MailboxId = rule.MailboxId;
        if (!await LoadMailboxAsync())
        {
            return NotFound();
        }

        db.MailRules.Remove(rule);
        await db.SaveChangesAsync();
        this.Notify(l["The rule was deleted."].Value);
        return RedirectToPage("Index", new { MailboxId });
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Finds the mailbox and the folders to choose from; false when the user may not change the rules of this mailbox.</summary>
    private async Task<bool> LoadMailboxAsync()
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return false;
        }

        IReadOnlyList<AccessibleMailbox> boxes = await access.GetMailboxesAsync(user);
        AccessibleMailbox? mailbox = boxes.FirstOrDefault(b => b.Mailbox.Id == MailboxId && b.Access >= MailboxAccess.Manage && b.Mailbox.Type != MailboxType.Unassigned)
                                     ?? (MailboxId == 0 ? boxes.FirstOrDefault(b => b.IsOwn) : null);
        if (mailbox is null)
        {
            return false;
        }

        MailboxId = mailbox.Mailbox.Id;
        MailboxName = mailbox.IsOwn ? l["My mailbox"].Value : mailbox.Mailbox.Name;

        List<MailFolder> folders = await db.MailFolders.AsNoTracking().Where(f => f.MailboxId == MailboxId).ToListAsync();
        IReadOnlyDictionary<long, string> paths = Labels.FolderPaths(folders);
        FolderItems = FolderService.Arrange(folders).Select(f => new SelectListItem(paths[f.Id], f.Id.ToString())).ToList();
        return true;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", l["Name is required."]);
        }
        else if (Input.Name.Trim().Length > 200)
        {
            ModelState.AddModelError("Input.Name", l["The name is too long."]);
        }

        if (Input.Conditions.Count == 0)
        {
            ModelState.AddModelError(string.Empty, l["A rule needs at least one condition."]);
        }

        if (Input.Conditions.Count > MaxConditions)
        {
            ModelState.AddModelError(string.Empty, l["A rule can have {0} conditions at most.", MaxConditions]);
        }

        for (int i = 0; i < Math.Min(Input.Conditions.Count, MaxConditions); i++)
        {
            string? problem = Check(Input.Conditions[i]);
            if (problem is not null)
            {
                ModelState.AddModelError(string.Empty, l["Condition {0}: {1}", i + 1, problem]);
            }
        }

        if (Input.Actions.Count == 0)
        {
            ModelState.AddModelError(string.Empty, l["A rule needs at least one action."]);
        }

        if (Input.Actions.Count > MaxActions)
        {
            ModelState.AddModelError(string.Empty, l["A rule can have {0} actions at most.", MaxActions]);
        }

        for (int i = 0; i < Math.Min(Input.Actions.Count, MaxActions); i++)
        {
            string? problem = Check(Input.Actions[i]);
            if (problem is not null)
            {
                ModelState.AddModelError(string.Empty, l["Action {0}: {1}", i + 1, problem]);
            }
        }
    }

    private string? Check(ConditionInput c)
    {
        string value = (c.Value ?? string.Empty).Trim();
        switch (c.Field)
        {
            case RuleField.HasAttachment:
                return value is "true" or "false" ? null : l["Choose yes or no."].Value;
            case RuleField.Size:
                return TryParseSize(value, out _) ? null : l["Enter the size in kilobytes (a number)."].Value;
        }

        if (c.Field == RuleField.Header && string.IsNullOrWhiteSpace(c.HeaderName))
        {
            return l["Enter the name of the header field, for example List-Id."].Value;
        }

        if (c.Field == RuleField.Header && c.HeaderName!.Trim().Any(ch => ch <= ' ' || ch == ':' || ch > '~'))
        {
            return l["The name of a header field has no blanks and no colon."].Value;
        }

        if (value.Length == 0)
        {
            return l["Enter the text to look for."].Value;
        }

        return value.Length > 500 ? l["The text is too long."].Value : null;
    }

    private string? Check(ActionInput a)
    {
        switch (a.Type)
        {
            case RuleActionType.MoveToFolder:
                return a.FolderId is long id && FolderItems.Any(f => f.Value == id.ToString()) ? null : l["Choose a folder."].Value;
            case RuleActionType.AddLabel:
                return MailRuleEngine.ToKeyword(a.Value) is null ? l["Enter a label (letters and digits)."].Value : null;
            case RuleActionType.ForwardTo:
                string address = MailAddresses.Normalize(a.Value);
                return MailAddresses.IsValid(address) && !MailAddresses.IsCatchAll(address) ? null : l["Enter a valid e-mail address to forward to."].Value;
            default:
                return null;
        }
    }

    private static MailRuleCondition ToEntity(ConditionInput c, long tenantId)
    {
        string value = (c.Value ?? string.Empty).Trim();
        RuleOperator op = RuleLabels.OperatorsFor(c.Field).Contains(c.Operator) ? c.Operator : RuleLabels.OperatorsFor(c.Field)[0];
        if (c.Field == RuleField.Size && TryParseSize(value, out double kilobytes))
        {
            value = kilobytes.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return new MailRuleCondition
        {
            TenantId = tenantId,
            Field = c.Field,
            Operator = op,
            Value = value,
            HeaderName = c.Field == RuleField.Header ? c.HeaderName!.Trim() : null,
        };
    }

    private static MailRuleAction ToEntity(ActionInput a, long tenantId) => new()
    {
        TenantId = tenantId,
        Type = a.Type,
        FolderId = a.Type == RuleActionType.MoveToFolder ? a.FolderId : null,
        Value = a.Type switch
        {
            RuleActionType.ForwardTo => MailAddresses.Normalize(a.Value),
            RuleActionType.AddLabel => a.Value!.Trim(),
            _ => null,
        },
    };

    private static bool TryParseSize(string value, out double kilobytes)
        => double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out kilobytes) && kilobytes >= 0 && kilobytes <= 10_000_000;
}

using MatMail.Data;
using MatMail.Messaging;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account.Rules;

/// <summary>The words of the rule pages: what the conditions, operators and actions are called, and a rule in one readable line.</summary>
public sealed class RuleLabels(IStringLocalizer<SharedResource> l)
{
    public string Field(RuleField field) => field switch
    {
        RuleField.From => l["From"].Value,
        RuleField.To => l["To or Cc"].Value,
        RuleField.Subject => l["Subject"].Value,
        RuleField.Body => l["Text of the message"].Value,
        RuleField.Header => l["Header field"].Value,
        RuleField.HasAttachment => l["Attachment"].Value,
        RuleField.Size => l["Size"].Value,
        _ => field.ToString(),
    };

    public string Operator(RuleOperator op) => op switch
    {
        RuleOperator.Contains => l["contains"].Value,
        RuleOperator.DoesNotContain => l["does not contain"].Value,
        RuleOperator.Equals => l["is"].Value,
        RuleOperator.StartsWith => l["starts with"].Value,
        RuleOperator.EndsWith => l["ends with"].Value,
        RuleOperator.LargerThan => l["is larger than"].Value,
        RuleOperator.SmallerThan => l["is smaller than"].Value,
        _ => op.ToString(),
    };

    public string Action(RuleActionType type) => type switch
    {
        RuleActionType.MoveToFolder => l["Move to folder"].Value,
        RuleActionType.MarkAsRead => l["Mark as read"].Value,
        RuleActionType.Star => l["Star it"].Value,
        RuleActionType.AddLabel => l["Add a label"].Value,
        RuleActionType.MoveToTrash => l["Move to the trash"].Value,
        RuleActionType.Discard => l["Delete for good"].Value,
        RuleActionType.ForwardTo => l["Forward a copy to"].Value,
        _ => type.ToString(),
    };

    /// <summary>The operators that make sense for a field.</summary>
    public static IReadOnlyList<RuleOperator> OperatorsFor(RuleField field) => field switch
    {
        RuleField.Size => new[] { RuleOperator.LargerThan, RuleOperator.SmallerThan },
        RuleField.HasAttachment => new[] { RuleOperator.Equals },
        _ => new[] { RuleOperator.Contains, RuleOperator.DoesNotContain, RuleOperator.Equals, RuleOperator.StartsWith, RuleOperator.EndsWith },
    };

    public IReadOnlyList<SelectListItem> FieldItems()
        => Enum.GetValues<RuleField>().Select(f => new SelectListItem(Field(f), f.ToString())).ToList();

    public IReadOnlyList<SelectListItem> OperatorItems(RuleField field)
        => OperatorsFor(field).Select(o => new SelectListItem(Operator(o), o.ToString())).ToList();

    public IReadOnlyList<SelectListItem> ActionItems()
        => Enum.GetValues<RuleActionType>().Select(a => new SelectListItem(Action(a), a.ToString())).ToList();

    public IReadOnlyList<SelectListItem> MatchItems() => new[]
    {
        new SelectListItem(l["All of these conditions"].Value, nameof(RuleMatch.All)),
        new SelectListItem(l["At least one of these conditions"].Value, nameof(RuleMatch.Any)),
    };

    public IReadOnlyList<SelectListItem> YesNoItems() => new[]
    {
        new SelectListItem(l["Yes"].Value, "true"),
        new SelectListItem(l["No"].Value, "false"),
    };

    /// <summary>A system folder is called what the web client calls it; the others by their name.</summary>
    public string FolderName(FolderKind kind, string name) => kind switch
    {
        FolderKind.Inbox => l["Inbox"].Value,
        FolderKind.Drafts => l["Drafts"].Value,
        FolderKind.Sent => l["Sent"].Value,
        FolderKind.Archive => l["Archive"].Value,
        FolderKind.Junk => l["Spam"].Value,
        FolderKind.Trash => l["Trash"].Value,
        _ => name,
    };

    /// <summary>The folders of a mailbox with their names for a list: "Inbox", "Inbox / Clients", …</summary>
    public IReadOnlyDictionary<long, string> FolderPaths(IEnumerable<MailFolder> folders)
    {
        List<MailFolder> all = folders.ToList();
        Dictionary<long, MailFolder> byId = all.ToDictionary(f => f.Id);
        return all.ToDictionary(f => f.Id, f =>
        {
            var parts = new Stack<string>();
            for (MailFolder? current = f; current is not null && parts.Count < FolderService.MaxDepth; current = current.ParentId is long p && byId.TryGetValue(p, out MailFolder? up) ? up : null)
            {
                parts.Push(FolderName(current.Kind, current.Name));
            }

            return string.Join(" / ", parts);
        });
    }

    public string Describe(MailRuleCondition condition)
    {
        string text = condition.Field switch
        {
            RuleField.HasAttachment => condition.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? l["The message has an attachment"].Value : l["The message has no attachment"].Value,
            RuleField.Size => $"{Field(RuleField.Size)} {Operator(condition.Operator)} {condition.Value} KB",
            RuleField.Header => $"{condition.HeaderName} {Operator(condition.Operator)} “{condition.Value}”",
            _ => $"{Field(condition.Field)} {Operator(condition.Operator)} “{condition.Value}”",
        };
        return text;
    }

    public string Describe(MailRuleAction action, IReadOnlyDictionary<long, string> folders) => action.Type switch
    {
        RuleActionType.MoveToFolder => action.FolderId is long id && folders.TryGetValue(id, out string? path)
            ? $"{Action(action.Type)} {path}"
            : l["Move to a folder that does not exist any more"].Value,
        RuleActionType.AddLabel => $"{Action(action.Type)} “{action.Value}”",
        RuleActionType.ForwardTo => $"{Action(action.Type)} {action.Value}",
        _ => Action(action.Type),
    };
}

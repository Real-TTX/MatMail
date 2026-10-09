namespace MatMail.Data;

/// <summary>
/// A rule of one mailbox: when a message arrives and matches the conditions, the actions are carried out (move it, mark it, star it,
/// label it, delete it, forward a copy). Rules run in the order of <see cref="Position"/>.
/// </summary>
public class MailRule : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }

    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>The order the rules of a mailbox are applied in (lowest first).</summary>
    public int Position { get; set; }

    /// <summary>All conditions have to fit, or one of them is enough.</summary>
    public RuleMatch Match { get; set; } = RuleMatch.All;

    /// <summary>The rules after this one are not looked at for a message that matched it.</summary>
    public bool StopProcessing { get; set; }

    /// <summary>How many messages the rule has been applied to, and when last (shown in the list, so a rule that never fires is noticed).</summary>
    public long MatchCount { get; set; }
    public DateTime? LastMatchDate { get; set; }

    public List<MailRuleCondition> Conditions { get; set; } = new();
    public List<MailRuleAction> Actions { get; set; } = new();
}

public class MailRuleCondition : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long RuleId { get; set; }
    public MailRule? Rule { get; set; }

    public RuleField Field { get; set; } = RuleField.From;
    public RuleOperator Operator { get; set; } = RuleOperator.Contains;

    /// <summary>The text to look for; for a size the number of kilobytes; for "has attachment" true or false.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The name of the header field for <see cref="RuleField.Header"/> (e.g. "List-Id").</summary>
    public string? HeaderName { get; set; }
}

public class MailRuleAction : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public long RuleId { get; set; }
    public MailRule? Rule { get; set; }

    public RuleActionType Type { get; set; } = RuleActionType.MoveToFolder;

    /// <summary>The folder for <see cref="RuleActionType.MoveToFolder"/>. Null after that folder was deleted: the action then does nothing.</summary>
    public long? FolderId { get; set; }

    /// <summary>The address for <see cref="RuleActionType.ForwardTo"/>, the label for <see cref="RuleActionType.AddLabel"/>.</summary>
    public string? Value { get; set; }
}

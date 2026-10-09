using MatMail.Data;
using MatMail.Messaging;
using MatMail.Pages.Account.Rules;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MatMail.Tests;

/// <summary>The pages where people manage the rules of their mailboxes: who may, and what the editor accepts.</summary>
public class RulePageTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static T Prepare<T>(T page) where T : PageModel
    {
        var http = new DefaultHttpContext();
        page.PageContext = new PageContext { HttpContext = http };
        page.TempData = new TempDataDictionary(http, new NoTempData());
        return page;
    }

    private static IndexModel IndexOf(IServiceScope scope)
        => Prepare(new IndexModel(
            scope.ServiceProvider.GetRequiredService<MatMailDbContext>(), scope.ServiceProvider.GetRequiredService<MailAccessService>(),
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()));

    private static EditModel EditOf(IServiceScope scope)
        => Prepare(new EditModel(
            scope.ServiceProvider.GetRequiredService<MatMailDbContext>(), scope.ServiceProvider.GetRequiredService<MailAccessService>(),
            scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()));

    private async Task<long> AddRuleAsync(Mailbox mailbox, string name = "A rule", bool active = true)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var rule = new MailRule
        {
            TenantId = _seed.Tenant.Id, MailboxId = mailbox.Id, Name = name, IsActive = active,
            Conditions = { new MailRuleCondition { TenantId = _seed.Tenant.Id, Field = RuleField.From, Operator = RuleOperator.Contains, Value = "shop" } },
            Actions = { new MailRuleAction { TenantId = _seed.Tenant.Id, Type = RuleActionType.Star } },
        };
        db.MailRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private async Task GiveAccessAsync(User user, Mailbox mailbox, MailboxAccess access)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.MailboxPermissions.Add(new MailboxPermission { TenantId = mailbox.TenantId, MailboxId = mailbox.Id, UserId = user.Id, Access = access });
        await db.SaveChangesAsync();
    }

    private async Task<MailRule> RuleAsync(long id)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRules.AsNoTracking().Include(r => r.Conditions).Include(r => r.Actions).SingleAsync(r => r.Id == id);
    }

    private async Task<int> CountRulesAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRules.CountAsync();
    }

    private static EditModel.InputModel ValidInput(long folderId) => new()
    {
        Name = "Shop mail",
        Match = RuleMatch.Any,
        StopProcessing = true,
        Conditions =
        {
            new EditModel.ConditionInput { Field = RuleField.From, Operator = RuleOperator.Contains, Value = " shop.test " },
            new EditModel.ConditionInput { Field = RuleField.HasAttachment, Operator = RuleOperator.Contains, Value = "true" },
            new EditModel.ConditionInput { Field = RuleField.Size, Operator = RuleOperator.SmallerThan, Value = "1,5" },
            new EditModel.ConditionInput { Field = RuleField.Header, Operator = RuleOperator.Equals, Value = "yes", HeaderName = " X-Spam-Flag " },
        },
        Actions =
        {
            new EditModel.ActionInput { Type = RuleActionType.MoveToFolder, FolderId = folderId, Value = "stale text" },
            new EditModel.ActionInput { Type = RuleActionType.ForwardTo, Value = " Friend@Outside.test " },
            new EditModel.ActionInput { Type = RuleActionType.AddLabel, Value = "Shop Mail" },
            new EditModel.ActionInput { Type = RuleActionType.MarkAsRead, FolderId = folderId, Value = "ignored" },
        },
    };

    // ----- who may ---------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Somebody_without_access_to_the_mailbox_cannot_change_its_rules()
    {
        long id = await AddRuleAsync(_seed.AliceMailbox);

        using IServiceScope bob = _host.ScopeAs(_seed.Bob);
        Assert.IsType<NotFoundResult>(await IndexOf(bob).OnPostToggleAsync(id));
        Assert.IsType<NotFoundResult>(await IndexOf(bob).OnPostMoveAsync(id, "down"));

        EditModel get = EditOf(bob);
        get.Id = id;
        Assert.IsType<NotFoundResult>(await get.OnGetAsync());

        EditModel post = EditOf(bob);
        post.Id = id;
        post.Input = ValidInput(0);
        Assert.IsType<NotFoundResult>(await post.OnPostAsync());

        EditModel delete = EditOf(bob);
        delete.Id = id;
        Assert.IsType<NotFoundResult>(await delete.OnPostDeleteAsync());

        EditModel create = EditOf(bob);
        create.MailboxId = _seed.AliceMailbox.Id;
        Assert.IsType<NotFoundResult>(await create.OnGetAsync());

        MailRule unchanged = await RuleAsync(id);
        Assert.True(unchanged.IsActive);
        Assert.Equal("A rule", unchanged.Name);
    }

    [DbFact]
    public async Task Full_control_of_a_shared_mailbox_is_needed_and_enough()
    {
        long id = await AddRuleAsync(_seed.Info);
        await GiveAccessAsync(_seed.Bob, _seed.Info, MailboxAccess.Send);   // may write, but not manage

        using (IServiceScope bob = _host.ScopeAs(_seed.Bob))
        {
            Assert.IsType<NotFoundResult>(await IndexOf(bob).OnPostMoveAsync(id, "up"));
        }

        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailboxPermissions.Where(p => p.UserId == _seed.Bob.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Access, MailboxAccess.Manage));
        }

        using IServiceScope manager = _host.ScopeAs(_seed.Bob);
        IndexModel page = IndexOf(manager);
        Assert.IsType<RedirectToPageResult>(await page.OnPostToggleAsync(id));
        Assert.False((await RuleAsync(id)).IsActive);
    }

    [DbFact]
    public async Task An_administrator_does_not_get_the_rules_of_other_peoples_mailboxes_through_the_role()
    {
        long id = await AddRuleAsync(_seed.AliceMailbox);

        using IServiceScope admin = _host.ScopeAs(_seed.Bob, Permissions.MailUse, Permissions.MailboxesManage, Permissions.UsersManage);
        Assert.IsType<NotFoundResult>(await IndexOf(admin).OnPostToggleAsync(id));
    }

    [DbFact]
    public async Task The_list_offers_only_mailboxes_the_user_manages()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Mine");
        await AddRuleAsync(_seed.Info, "Shared");
        await GiveAccessAsync(_seed.Alice, _seed.Info, MailboxAccess.Edit);

        using (IServiceScope alice = _host.ScopeAs(_seed.Alice))
        {
            IndexModel page = IndexOf(alice);
            await page.OnGetAsync();
            Assert.Equal(new[] { _seed.AliceMailbox.Id.ToString() }, page.MailboxItems.Select(i => i.Value));
            Assert.Equal("Mine", Assert.Single(page.Rules).Name);
        }

        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailboxPermissions.Where(p => p.UserId == _seed.Alice.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Access, MailboxAccess.Manage));
        }

        using IServiceScope manager = _host.ScopeAs(_seed.Alice);
        IndexModel both = IndexOf(manager);
        both.MailboxId = _seed.Info.Id;
        await both.OnGetAsync();
        Assert.Equal(2, both.MailboxItems.Count);
        Assert.Equal("Shared", Assert.Single(both.Rules).Name);
    }

    [DbFact]
    public async Task Rules_move_up_and_down_and_are_numbered_afresh()
    {
        long first = await AddRuleAsync(_seed.AliceMailbox, "First");
        long second = await AddRuleAsync(_seed.AliceMailbox, "Second");
        long third = await AddRuleAsync(_seed.AliceMailbox, "Third");

        using (IServiceScope alice = _host.ScopeAs(_seed.Alice))
        {
            await IndexOf(alice).OnPostMoveAsync(third, "up");
            await IndexOf(alice).OnPostMoveAsync(first, "up");   // already on top: nothing happens
        }

        using IServiceScope check = _host.ScopeAs(_seed.Alice);
        IndexModel page = IndexOf(check);
        await page.OnGetAsync();
        Assert.Equal(new[] { "First", "Third", "Second" }, page.Rules.Select(r => r.Name));
        Assert.Equal(new[] { first, third, second }, page.Rules.Select(r => r.Id));
    }

    // ----- the editor ------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_editor_saves_a_rule_and_tidies_what_was_typed()
    {
        long folder;
        using (IServiceScope scope = _host.Scope())
        {
            folder = (await scope.ServiceProvider.GetRequiredService<FolderService>().EnsureAsync(_seed.AliceMailbox.Id, "Shop")).Id;
        }

        using IServiceScope alice = _host.ScopeAs(_seed.Alice);
        EditModel page = EditOf(alice);
        page.Input = ValidInput(folder);
        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());

        Assert.True(page.ModelState.IsValid);
        Assert.Equal("Index", result.PageName);
        MailRule rule = await RuleAsync(await FirstRuleIdAsync());
        Assert.Equal(("Shop mail", RuleMatch.Any, true, true, 0), (rule.Name, rule.Match, rule.StopProcessing, rule.IsActive, rule.Position));
        Assert.Equal(_seed.AliceMailbox.Id, rule.MailboxId);

        List<MailRuleCondition> conditions = rule.Conditions.OrderBy(c => c.Id).ToList();
        Assert.Equal(("shop.test", RuleOperator.Contains), (conditions[0].Value, conditions[0].Operator));
        Assert.Equal((RuleField.HasAttachment, RuleOperator.Equals, "true"), (conditions[1].Field, conditions[1].Operator, conditions[1].Value));   // the operator fits the field
        Assert.Equal("1.5", conditions[2].Value);
        Assert.Equal(("X-Spam-Flag", "yes"), (conditions[3].HeaderName, conditions[3].Value));
        Assert.Null(conditions[0].HeaderName);

        List<MailRuleAction> actions = rule.Actions.OrderBy(a => a.Id).ToList();
        Assert.Equal((folder, (string?)null), (actions[0].FolderId, actions[0].Value));
        Assert.Equal(("friend@outside.test", (long?)null), (actions[1].Value, actions[1].FolderId));
        Assert.Equal("Shop Mail", actions[2].Value);
        Assert.Equal(((long?)null, (string?)null), (actions[3].FolderId, actions[3].Value));   // what does not belong to the action is not kept
    }

    private async Task<long> FirstRuleIdAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRules.Select(r => r.Id).SingleAsync();
    }

    [DbFact]
    public async Task The_editor_refuses_what_cannot_work()
    {
        long bobsFolder;
        using (IServiceScope scope = _host.Scope())
        {
            bobsFolder = (await scope.ServiceProvider.GetRequiredService<FolderService>().EnsureAsync(_seed.BobMailbox.Id, "Bob only")).Id;
        }

        using IServiceScope alice = _host.ScopeAs(_seed.Alice);
        EditModel page = EditOf(alice);
        page.Input = new EditModel.InputModel
        {
            Name = " ",
            Conditions =
            {
                new EditModel.ConditionInput { Field = RuleField.Subject, Value = "  " },
                new EditModel.ConditionInput { Field = RuleField.Size, Value = "lots" },
                new EditModel.ConditionInput { Field = RuleField.HasAttachment, Value = "maybe" },
                new EditModel.ConditionInput { Field = RuleField.Header, Value = "x", HeaderName = "Bad Name:" },
                new EditModel.ConditionInput { Field = RuleField.Header, Value = "x", HeaderName = "" },
            },
            Actions =
            {
                new EditModel.ActionInput { Type = RuleActionType.MoveToFolder, FolderId = bobsFolder },   // a folder of another mailbox
                new EditModel.ActionInput { Type = RuleActionType.MoveToFolder, FolderId = null },
                new EditModel.ActionInput { Type = RuleActionType.ForwardTo, Value = "not an address" },
                new EditModel.ActionInput { Type = RuleActionType.ForwardTo, Value = "*@example.test" },
                new EditModel.ActionInput { Type = RuleActionType.AddLabel, Value = "()" },
            },
        };

        Assert.IsType<PageResult>(await page.OnPostAsync());

        Assert.False(page.ModelState.IsValid);
        Assert.Equal(11, page.ModelState.ErrorCount);   // the name, five conditions, five actions
        Assert.Equal(0, await CountRulesAsync());
    }

    [DbFact]
    public async Task A_rule_needs_a_name_a_condition_and_an_action()
    {
        using IServiceScope alice = _host.ScopeAs(_seed.Alice);
        EditModel page = EditOf(alice);
        page.Input = new EditModel.InputModel { Name = "Empty" };

        Assert.IsType<PageResult>(await page.OnPostAsync());

        Assert.Equal(2, page.ModelState.ErrorCount);
        Assert.Equal(0, await CountRulesAsync());
    }

    [DbFact]
    public async Task Editing_replaces_conditions_and_actions_and_keeps_the_place_of_the_rule()
    {
        await AddRuleAsync(_seed.AliceMailbox, "First");
        long second = await AddRuleAsync(_seed.AliceMailbox, "Second");
        long folder;
        using (IServiceScope scope = _host.Scope())
        {
            folder = (await scope.ServiceProvider.GetRequiredService<FolderService>().EnsureAsync(_seed.AliceMailbox.Id, "Shop")).Id;
            await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRules.Where(r => r.Id == second).ExecuteUpdateAsync(s => s.SetProperty(r => r.Position, 7));
        }

        using (IServiceScope alice = _host.ScopeAs(_seed.Alice))
        {
            EditModel get = EditOf(alice);
            get.Id = second;
            Assert.IsType<PageResult>(await get.OnGetAsync());
            Assert.Equal("Second", get.Input.Name);
            Assert.Equal("shop", Assert.Single(get.Input.Conditions).Value);

            EditModel post = EditOf(alice);
            post.Id = second;
            post.Input = ValidInput(folder);
            Assert.IsType<RedirectToPageResult>(await post.OnPostAsync());
        }

        MailRule rule = await RuleAsync(second);
        Assert.Equal(("Shop mail", 7), (rule.Name, rule.Position));
        Assert.Equal(4, rule.Conditions.Count);
        Assert.Equal(4, rule.Actions.Count);
        using IServiceScope check = _host.Scope();
        var db = check.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal(4, await db.MailRuleConditions.CountAsync(c => c.RuleId == second));   // the old ones are gone, nobody else's were touched
        Assert.Equal(1 + 4, await db.MailRuleConditions.CountAsync());
    }

    [DbFact]
    public async Task A_rule_can_be_deleted_with_all_it_holds()
    {
        long id = await AddRuleAsync(_seed.AliceMailbox);

        using (IServiceScope alice = _host.ScopeAs(_seed.Alice))
        {
            EditModel page = EditOf(alice);
            page.Id = id;
            Assert.IsType<RedirectToPageResult>(await page.OnPostDeleteAsync());
        }

        using IServiceScope check = _host.Scope();
        var db = check.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Assert.Equal((0, 0, 0), (await db.MailRules.CountAsync(), await db.MailRuleConditions.CountAsync(), await db.MailRuleActions.CountAsync()));
    }

    [DbFact]
    public async Task Rules_go_with_their_mailbox()
    {
        await AddRuleAsync(_seed.Info);

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        await db.Mailboxes.Where(m => m.Id == _seed.Info.Id).ExecuteDeleteAsync();

        Assert.Equal((0, 0, 0), (await db.MailRules.CountAsync(), await db.MailRuleConditions.CountAsync(), await db.MailRuleActions.CountAsync()));
    }
}

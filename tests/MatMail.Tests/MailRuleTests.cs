using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>The conditions of mailbox rules, without a database.</summary>
public class MailRuleConditionTests
{
    private static MailRuleEngine.MessageFacts Facts(byte[] raw, string[]? envelope = null, bool onlyHeaders = false)
        => new(raw, MessageParser.Parse(raw), envelope ?? Array.Empty<string>(), onlyHeaders);

    private static MailRuleCondition Condition(RuleField field, RuleOperator op, string value, string? header = null)
        => new() { Field = field, Operator = op, Value = value, HeaderName = header };

    private static MailRule Rule(RuleMatch match, params MailRuleCondition[] conditions) => new() { Match = match, Conditions = conditions.ToList() };

    private static byte[] Plain(string from = "Shop News <newsletter@shop.test>", string to = "alice@example.test", string subject = "Your weekly offers", string body = "Cheap things.", string? extra = null, string? cc = null)
        => RawMail.Build(from, to, subject, body, extraHeaders: extra, cc: cc);

    private static bool Matches(MailRule rule, byte[] raw, string[]? envelope = null, bool onlyHeaders = false)
        => MailRuleEngine.Matches(rule, Facts(raw, envelope, onlyHeaders));

    [Fact]
    public void The_sender_is_found_by_name_or_address_whatever_the_case()
    {
        byte[] raw = Plain();
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.Contains, "NEWSLETTER@")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.Contains, "shop news")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.Equals, "newsletter@shop.test")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.Equals, "shop.test")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.EndsWith, "@shop.test")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.DoesNotContain, "boss@")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.From, RuleOperator.DoesNotContain, "newsletter")), raw));
    }

    [Fact]
    public void The_subject_can_start_or_end_with_a_text()
    {
        byte[] raw = Plain();
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Subject, RuleOperator.StartsWith, "your weekly")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Subject, RuleOperator.EndsWith, "offers")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Subject, RuleOperator.StartsWith, "offers")), raw));
    }

    [Fact]
    public void The_recipients_are_to_cc_and_what_the_message_was_routed_for()
    {
        byte[] raw = Plain(cc: "carol@example.test");
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.To, RuleOperator.Contains, "alice@")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.To, RuleOperator.Equals, "carol@example.test")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.To, RuleOperator.Contains, "info@")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.To, RuleOperator.Equals, "info@example.test")), raw, new[] { "info@example.test" }));
    }

    [Fact]
    public void All_conditions_or_one_of_them()
    {
        byte[] raw = Plain();
        MailRuleCondition yes = Condition(RuleField.Subject, RuleOperator.Contains, "weekly");
        MailRuleCondition no = Condition(RuleField.Subject, RuleOperator.Contains, "invoice");
        Assert.False(Matches(Rule(RuleMatch.All, yes, no), raw));
        Assert.True(Matches(Rule(RuleMatch.Any, yes, no), raw));
        Assert.True(Matches(Rule(RuleMatch.All, yes, yes), raw));
        Assert.False(Matches(Rule(RuleMatch.Any, no, no), raw));
    }

    [Fact]
    public void A_rule_without_conditions_or_with_an_empty_text_matches_nothing()
    {
        byte[] raw = Plain();
        Assert.False(Matches(Rule(RuleMatch.All), raw));
        Assert.False(Matches(Rule(RuleMatch.Any), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Subject, RuleOperator.Contains, "  ")), raw));
    }

    [Fact]
    public void Header_fields_are_looked_up_by_name()
    {
        byte[] raw = Plain(extra: "List-Id: <news.shop.test>\r\nX-Spam-Flag: YES\r\n");
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Header, RuleOperator.Contains, "shop.test", "list-id")), raw));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Header, RuleOperator.Equals, "yes", "X-Spam-Flag:")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Header, RuleOperator.Contains, "x", "X-Missing")), raw));

        // What is not there cannot satisfy a condition, a negative one included.
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Header, RuleOperator.DoesNotContain, "x", "X-Missing")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Header, RuleOperator.Contains, "x", null)), raw));
    }

    [Fact]
    public void The_text_of_the_message_is_searched_when_it_is_here()
    {
        byte[] raw = Plain(body: "Please find the Invoice 4711 attached.");
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Body, RuleOperator.Contains, "invoice 4711")), raw));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Body, RuleOperator.Contains, "invoice 4711")), raw, onlyHeaders: true));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Body, RuleOperator.DoesNotContain, "refund")), raw, onlyHeaders: true));
    }

    [Fact]
    public void Attachments_and_size()
    {
        byte[] plain = Plain();
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "see attachment" } };
        multipart.Add(new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream(new byte[6000])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "a.pdf" });
        var message = new MimeMessage { Subject = "With file", Body = multipart };
        message.From.Add(MailboxAddress.Parse("max@sender.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        byte[] withFile = MimeSerializer.ToBytes(message);

        MailRule hasAttachment = Rule(RuleMatch.All, Condition(RuleField.HasAttachment, RuleOperator.Equals, "true"));
        Assert.False(Matches(hasAttachment, plain));
        Assert.True(Matches(hasAttachment, withFile));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.HasAttachment, RuleOperator.Equals, "false")), plain));
        Assert.False(Matches(hasAttachment, withFile, onlyHeaders: true));

        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Size, RuleOperator.LargerThan, "5")), withFile));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Size, RuleOperator.LargerThan, "5")), plain));
        Assert.True(Matches(Rule(RuleMatch.All, Condition(RuleField.Size, RuleOperator.SmallerThan, "5")), plain));
        Assert.False(Matches(Rule(RuleMatch.All, Condition(RuleField.Size, RuleOperator.SmallerThan, "lots")), plain));
    }

    [Theory]
    [InlineData("Rechnungen 2026", "Rechnungen_2026")]
    [InlineData("$Important", "$Important")]
    [InlineData("  work   stuff ", "work_stuff")]
    [InlineData("a/b(c)", "abc")]
    [InlineData("   ", null)]
    [InlineData("()", null)]
    [InlineData(null, null)]
    public void Labels_become_imap_keywords(string? label, string? expected) => Assert.Equal(expected, MailRuleEngine.ToKeyword(label));

    [Fact]
    public void Long_labels_are_cut() => Assert.Equal(40, MailRuleEngine.ToKeyword(new string('x', 100))!.Length);

    [Fact]
    public void Redirected_messages_carry_resent_headers_in_front_and_count_their_hops()
    {
        byte[] raw = Plain();
        byte[] resent = MailRuleEngine.AddResentHeaders(raw, "alice@example.test", "bob@elsewhere.test", 3, "mail.example.test");

        string text = System.Text.Encoding.UTF8.GetString(resent);
        Assert.StartsWith("Resent-Date: ", text);
        Assert.Contains("Resent-From: <alice@example.test>\r\n", text);
        Assert.Contains("Resent-To: <bob@elsewhere.test>\r\n", text);
        Assert.Contains("@mail.example.test>\r\n", text);
        Assert.EndsWith(System.Text.Encoding.UTF8.GetString(raw), text);   // the message itself is not touched
        Assert.Equal(3, MailRuleEngine.HopsOf(resent));
        Assert.Equal(0, MailRuleEngine.HopsOf(raw));
    }
}

/// <summary>Rules at work in the delivery of mail to a mailbox.</summary>
public class MailRuleDeliveryTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ----- helpers ------------------------------------------------------------------------------------------------

    private async Task<long> AddRuleAsync(Mailbox mailbox, string name, Action<MailRule> configure, int position = 0)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var rule = new MailRule { TenantId = _seed.Tenant.Id, MailboxId = mailbox.Id, Name = name, Position = position };
        configure(rule);
        foreach (MailRuleCondition c in rule.Conditions) { c.TenantId = _seed.Tenant.Id; }
        foreach (MailRuleAction a in rule.Actions) { a.TenantId = _seed.Tenant.Id; }
        db.MailRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private static MailRuleCondition From(string text) => new() { Field = RuleField.From, Operator = RuleOperator.Contains, Value = text };
    private static MailRuleCondition Subject(string text) => new() { Field = RuleField.Subject, Operator = RuleOperator.Contains, Value = text };
    private static MailRuleAction Do(RuleActionType type, long? folderId = null, string? value = null) => new() { Type = type, FolderId = folderId, Value = value };

    private async Task<long> FolderAsync(Mailbox mailbox, string path)
    {
        using IServiceScope scope = _host.Scope();
        return (await scope.ServiceProvider.GetRequiredService<FolderService>().EnsureAsync(mailbox.Id, path)).Id;
    }

    private async Task<DeliveryResult> DeliverAsync(string subject, string to = "alice@example.test", string from = "newsletter@shop.test", string? extraHeaders = null, string body = "Hello", TransferChannel? channel = TransferChannel.SmtpServer)
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build(from, to, subject, body, extraHeaders: extraHeaders),
            new DeliverySource { EnvelopeRecipients = new[] { to }, Channel = channel, EnvelopeSender = from, RemoteIp = "203.0.113.7" });
    }

    private async Task<List<(MailMessage Message, FolderKind Kind, string Path)>> MessagesAsync(Mailbox mailbox)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        IReadOnlyList<FolderInfo> folders = await scope.ServiceProvider.GetRequiredService<FolderService>().ListAsync(mailbox.Id);
        List<MailMessage> messages = await db.MailMessages.AsNoTracking().Where(m => m.MailboxId == mailbox.Id).OrderBy(m => m.Id).ToListAsync();
        return messages.Select(m => (m, folders.First(f => f.Id == m.FolderId).Kind, folders.First(f => f.Id == m.FolderId).Path)).ToList();
    }

    private async Task<List<MailTransfer>> TransfersAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailTransfers.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
    }

    // ----- actions -------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_matching_rule_files_marks_stars_and_labels_the_message_as_it_arrives()
    {
        long folder = await FolderAsync(_seed.AliceMailbox, "Newsletters");
        long ruleId = await AddRuleAsync(_seed.AliceMailbox, "Newsletters", r =>
        {
            r.Conditions.Add(From("newsletter@"));
            r.Actions.Add(Do(RuleActionType.MoveToFolder, folder));
            r.Actions.Add(Do(RuleActionType.MarkAsRead));
            r.Actions.Add(Do(RuleActionType.Star));
            r.Actions.Add(Do(RuleActionType.AddLabel, value: "Shop Mail"));
        });

        DeliveryResult result = await DeliverAsync("Weekly offers");

        Assert.Equal(1, result.Delivered);
        var stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Equal("Newsletters", stored.Path);
        Assert.True(stored.Message.IsRead);
        Assert.True(stored.Message.IsStarred);
        Assert.Contains("Shop_Mail", stored.Message.Keywords);

        using IServiceScope scope = _host.Scope();
        MailRule rule = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRules.AsNoTracking().SingleAsync(r => r.Id == ruleId);
        Assert.Equal(1, rule.MatchCount);
        Assert.NotNull(rule.LastMatchDate);
    }

    [DbFact]
    public async Task A_message_that_fits_no_rule_lands_in_the_inbox_untouched()
    {
        long folder = await FolderAsync(_seed.AliceMailbox, "Newsletters");
        await AddRuleAsync(_seed.AliceMailbox, "Newsletters", r => { r.Conditions.Add(From("newsletter@")); r.Actions.Add(Do(RuleActionType.MoveToFolder, folder)); });

        await DeliverAsync("From a friend", from: "friend@elsewhere.test");

        var stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Equal(FolderKind.Inbox, stored.Kind);
        Assert.False(stored.Message.IsRead);
        Assert.False(stored.Message.IsStarred);
        Assert.Empty(stored.Message.Keywords);
    }

    [DbFact]
    public async Task A_rule_can_send_mail_to_the_trash_or_delete_it_for_good()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Trash", r => { r.Conditions.Add(Subject("to the trash")); r.Actions.Add(Do(RuleActionType.MoveToTrash)); }, 0);
        await AddRuleAsync(_seed.AliceMailbox, "Gone", r => { r.Conditions.Add(Subject("gone")); r.Actions.Add(Do(RuleActionType.Discard)); }, 1);

        await DeliverAsync("Off to the trash");
        DeliveryResult gone = await DeliverAsync("Simply gone");

        var stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Equal(FolderKind.Trash, stored.Kind);
        DeliveredCopy copy = Assert.Single(gone.Copies);
        Assert.Null(copy.Message);
        Assert.Equal(0, gone.Delivered);

        List<MailTransfer> log = await TransfersAsync();
        Assert.Equal(new[] { TransferStatus.Delivered, TransferStatus.Discarded }, log.Select(t => t.Status));
        Assert.Contains("deleted by a rule", log[1].Detail);
    }

    [DbFact]
    public async Task Rules_are_applied_from_the_top_until_one_ends_the_list()
    {
        long folder = await FolderAsync(_seed.AliceMailbox, "Invoices");
        await AddRuleAsync(_seed.AliceMailbox, "Invoices", r =>
        {
            r.Conditions.Add(Subject("invoice"));
            r.Actions.Add(Do(RuleActionType.MoveToFolder, folder));
            r.StopProcessing = true;
        }, 0);
        await AddRuleAsync(_seed.AliceMailbox, "Star everything", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.Star)); }, 1);

        await DeliverAsync("Your invoice");
        await DeliverAsync("Your offers");

        List<(MailMessage Message, FolderKind Kind, string Path)> stored = await MessagesAsync(_seed.AliceMailbox);
        Assert.Equal(2, stored.Count);
        Assert.Equal(("Invoices", false), (stored[0].Path, stored[0].Message.IsStarred));     // the first rule ended the list
        Assert.Equal((FolderKind.Inbox, true), (stored[1].Kind, stored[1].Message.IsStarred)); // the second applies to the other message
    }

    [DbFact]
    public async Task The_later_rule_wins_when_two_rules_move_the_same_message()
    {
        long first = await FolderAsync(_seed.AliceMailbox, "First");
        long second = await FolderAsync(_seed.AliceMailbox, "Second");
        await AddRuleAsync(_seed.AliceMailbox, "A", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.MoveToFolder, first)); }, 0);
        await AddRuleAsync(_seed.AliceMailbox, "B", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.MoveToFolder, second)); }, 1);

        await DeliverAsync("Offers");

        Assert.Equal("Second", Assert.Single(await MessagesAsync(_seed.AliceMailbox)).Path);
    }

    [DbFact]
    public async Task Rules_that_are_off_or_belong_to_another_mailbox_do_nothing()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Off", r => { r.IsActive = false; r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.Discard)); });
        await AddRuleAsync(_seed.BobMailbox, "Bob's", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.Discard)); });

        await DeliverAsync("Offers");

        Assert.Single(await MessagesAsync(_seed.AliceMailbox));
    }

    [DbFact]
    public async Task A_rule_with_a_folder_that_was_deleted_does_nothing_and_keeps_its_place()
    {
        long folder = await FolderAsync(_seed.AliceMailbox, "Gone soon");
        long ruleId = await AddRuleAsync(_seed.AliceMailbox, "Moves", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.MoveToFolder, folder)); r.Actions.Add(Do(RuleActionType.Star)); });
        using (IServiceScope scope = _host.Scope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<FolderService>().DeleteAsync(folder));
        }

        await DeliverAsync("Offers");

        var stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Equal(FolderKind.Inbox, stored.Kind);   // the move is skipped …
        Assert.True(stored.Message.IsStarred);          // … the rest of the rule is not

        using IServiceScope check = _host.Scope();
        MailRuleAction move = await check.ServiceProvider.GetRequiredService<MatMailDbContext>().MailRuleActions.AsNoTracking().SingleAsync(a => a.RuleId == ruleId && a.Type == RuleActionType.MoveToFolder);
        Assert.Null(move.FolderId);
    }

    [DbFact]
    public async Task Copies_for_the_unassigned_bucket_are_not_run_through_rules()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            Mailbox unassigned = await db.Mailboxes.AsNoTracking().SingleAsync(m => m.Id == _seed.UnassignedMailboxId);
            db.MailRules.Add(new MailRule
            {
                TenantId = _seed.Tenant.Id, MailboxId = unassigned.Id, Name = "Never", Conditions = { new MailRuleCondition { TenantId = _seed.Tenant.Id, Field = RuleField.From, Operator = RuleOperator.Contains, Value = "shop" } },
                Actions = { new MailRuleAction { TenantId = _seed.Tenant.Id, Type = RuleActionType.Discard } },
            });
            await db.SaveChangesAsync();
        }

        await DeliverAsync("Offers", to: "nobody@example.test");

        using IServiceScope check = _host.Scope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.CountAsync(m => m.MailboxId == _seed.UnassignedMailboxId));
    }

    // ----- forwarding ----------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_copy_forwarded_to_a_local_address_is_a_redirect_and_the_original_stays()
    {
        await AddRuleAsync(_seed.AliceMailbox, "To Bob", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "Bob@Example.test")); });

        await DeliverAsync("Offers");

        Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        var forwarded = Assert.Single(await MessagesAsync(_seed.BobMailbox));
        Assert.Equal(FolderKind.Inbox, forwarded.Kind);
        Assert.Equal("Offers", forwarded.Message.Subject);
        Assert.Equal("newsletter@shop.test", forwarded.Message.FromAddress);   // the sender is the original one

        using IServiceScope scope = _host.Scope();
        byte[] raw = (await scope.ServiceProvider.GetRequiredService<MailStore>().GetRawAsync(forwarded.Message.Id))!;
        string text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.Contains("Resent-From: <alice@example.test>", text);
        Assert.Contains("Resent-To: <bob@example.test>", text);
        Assert.Equal(1, MailRuleEngine.HopsOf(raw));
    }

    [DbFact]
    public async Task A_forward_to_a_full_local_mailbox_is_left_out_with_a_note_and_the_original_stays()
    {
        await AddRuleAsync(_seed.AliceMailbox, "To Bob", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "bob@example.test")); });
        await QuotaTestSupport.FillUpAsync(_host, _seed.BobMailbox.Id);

        DeliveryResult result = await DeliverAsync("Offers");

        Assert.Equal(1, result.Delivered);
        Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Single(await QuotaTestSupport.MessagesAsync(_host, _seed.BobMailbox.Id));   // the filler only
        using IServiceScope scope = _host.Scope();
        List<ActivityLog> log = await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().ActivityLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        Assert.Contains(log, l => l.Message.Contains("could not forward") && l.Message.Contains("is full"));
    }

    [DbFact]
    public async Task A_copy_forwarded_to_an_outside_address_goes_through_the_queue_from_an_address_of_the_mailbox()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Away", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "friend@outside.test")); });

        await DeliverAsync("Offers");

        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        OutboundMessage queued = await db.OutboundMessages.AsNoTracking().SingleAsync();
        Assert.Equal("alice@example.test", queued.EnvelopeFrom);
        Assert.Equal(new[] { "friend@outside.test" }, queued.Recipients);
        Assert.Contains("Resent-To: <friend@outside.test>", System.Text.Encoding.UTF8.GetString(queued.Raw));

        List<MailTransfer> log = await db.MailTransfers.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        MailTransfer outgoing = Assert.Single(log, t => t.Direction == TransferDirection.Outbound);
        Assert.Equal((TransferChannel.Rule, TransferStatus.Queued, queued.Id), (outgoing.Channel, outgoing.Status, outgoing.OutboundMessageId));
    }

    [DbFact]
    public async Task A_message_that_went_through_five_forwards_is_not_forwarded_again()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Away", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "friend@outside.test")); });

        await DeliverAsync("Offers", extraHeaders: "X-MatMail-Forward-Hops: 5\r\n");

        Assert.Single(await MessagesAsync(_seed.AliceMailbox));   // delivered, but not passed on
        using IServiceScope scope = _host.Scope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.CountAsync());
    }

    [DbFact]
    public async Task Two_mailboxes_that_forward_to_each_other_do_not_loop()
    {
        await AddRuleAsync(_seed.AliceMailbox, "To Bob", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "bob@example.test")); });
        await AddRuleAsync(_seed.BobMailbox, "To Alice", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "alice@example.test")); });

        await DeliverAsync("Offers");

        Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        Assert.Single(await MessagesAsync(_seed.BobMailbox));
    }

    [DbFact]
    public async Task Forwarding_to_the_own_address_is_left_out()
    {
        await AddRuleAsync(_seed.AliceMailbox, "Self", r => { r.Conditions.Add(From("shop")); r.Actions.Add(Do(RuleActionType.ForwardTo, value: "alice@example.test")); });

        await DeliverAsync("Offers");

        Assert.Single(await MessagesAsync(_seed.AliceMailbox));
        using IServiceScope scope = _host.Scope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.CountAsync());
    }

    // ----- the transfer log ----------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_delivery_from_another_server_is_written_to_the_transfer_log()
    {
        long folder = await FolderAsync(_seed.AliceMailbox, "Newsletters");
        await AddRuleAsync(_seed.AliceMailbox, "Newsletters", r => { r.Conditions.Add(From("newsletter@")); r.Actions.Add(Do(RuleActionType.MoveToFolder, folder)); });

        await DeliverAsync("Weekly offers");

        MailTransfer line = Assert.Single(await TransfersAsync());
        Assert.Equal((TransferDirection.Inbound, TransferChannel.SmtpServer, TransferStatus.Delivered), (line.Direction, line.Channel, line.Status));
        Assert.Equal("newsletter@shop.test", line.Sender);
        Assert.Equal("Weekly offers", line.Subject);
        Assert.Equal("203.0.113.7", line.RemoteIp);
        Assert.Equal(_seed.Tenant.Id, line.TenantId);
        Assert.Contains("Alice", line.Detail);
        Assert.Contains("1 rule(s) applied", line.Detail);
        Assert.True(line.SizeBytes > 0);
        Assert.NotNull(line.MessageIdHeader);
    }

    [DbFact]
    public async Task Mail_for_an_unknown_domain_without_a_tenant_is_logged_as_failed()
    {
        using IServiceScope scope = _host.Scope();
        DeliveryResult result = await scope.ServiceProvider.GetRequiredService<MailDelivery>().DeliverAsync(
            RawMail.Build("x@sender.test", "nobody@unknown.test", "Hello", "text"),
            new DeliverySource { EnvelopeRecipients = new[] { "nobody@unknown.test" }, Channel = TransferChannel.SmtpServer, RemoteIp = "203.0.113.8" });

        Assert.Empty(result.Copies);
        MailTransfer line = Assert.Single(await TransfersAsync());
        Assert.Equal((TransferStatus.Failed, (long?)null), (line.Status, line.TenantId));
        Assert.Contains("unknown.test", line.Detail);
    }

    [DbFact]
    public async Task The_transfer_log_can_be_switched_off_and_can_leave_out_subjects()
    {
        _host.Config.Retention.TransferLogDays = 0;
        await DeliverAsync("One");
        Assert.Empty(await TransfersAsync());

        _host.Config.Retention.TransferLogDays = 30;
        _host.Config.Retention.TransferLogSubjects = false;
        await DeliverAsync("Secret subject");
        MailTransfer line = Assert.Single(await TransfersAsync());
        Assert.Equal(string.Empty, line.Subject);
        Assert.Equal("newsletter@shop.test", line.Sender);
    }
}

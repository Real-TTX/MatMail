using MatMail.Data;
using MatMail.Messaging;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>The search text: operators in English and German, OR, minus, groups, dates, sizes.</summary>
public class MailQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static string Tree(string text) => MailQuery.Parse(1, null, text, Now).Describe();

    [Fact]
    public void Free_words_and_phrases_are_words_next_to_each_other()
    {
        Assert.Equal("AND(word:invoice, word:two words)", Tree("invoice \"two words\""));
        Assert.Equal("word:solo", Tree("  solo  "));
    }

    [Fact]
    public void Operators_are_terms_next_to_each_other()
    {
        Assert.Equal(
            "AND(from:alice, to:bob smith, subject:offer, has:attachment, is:unread, before:2026-01-31, word:invoice, word:two words)",
            Tree("from:alice to:\"bob smith\" subject:offer has:attachment is:unread before:2026-01-31 invoice \"two words\""));
        Assert.Equal("AND(is:read, is:starred, is:answered, is:draft)", Tree("is:read is:starred is:answered is:draft"));
    }

    [Fact]
    public void The_german_names_work_like_the_english_ones()
    {
        Assert.Equal(
            "AND(from:anna, to:ben, subject:angebot, has:attachment, is:unread, before:31.01.2026, after:2026-01-01)",
            Tree("von:anna an:ben betreff:angebot hat:anhang ist:ungelesen vor:31.01.2026 nach:2026-01-01"));
        Assert.Equal("AND(is:read, is:starred, is:answered, is:draft)", Tree("ist:gelesen ist:markiert ist:beantwortet ist:entwurf"));
        Assert.Equal("AND(older_than:2w, newer_than:1y, larger:5M, smaller:100k, in:Posteingang)", Tree("älter_als:2w neuer_als:1y größer:5M kleiner:100k ordner:Posteingang"));
    }

    [Fact]
    public void Or_and_the_bar_mean_either_and_bind_weaker_than_the_blank()
    {
        Assert.Equal("OR(from:anna, from:ben)", Tree("from:anna OR from:ben"));
        Assert.Equal("OR(word:a, word:b)", Tree("a | b"));
        Assert.Equal("OR(AND(word:a, word:b), word:c)", Tree("a b OR c"));
        Assert.Equal("OR(from:anna, from:ben)", Tree("von:anna ODER von:ben"));
        Assert.Equal("AND(word:or, word:and)", Tree("or and"));   // only the capitals are the operator
    }

    [Fact]
    public void A_minus_leaves_out_what_the_term_matches()
    {
        Assert.Equal("AND(NOT(from:anna), word:invoice)", Tree("-from:anna invoice"));
        Assert.Equal("NOT(word:two words)", Tree("-\"two words\""));
        Assert.Equal("NOT(AND(word:a, word:b))", Tree("-(a b)"));
        Assert.Equal("AND(word:e-mail, word:well-known)", Tree("e-mail well-known"));   // inside a word it is just a hyphen
        Assert.Equal("NOT(is:unread)", Tree("-is:unread"));
    }

    [Fact]
    public void Brackets_group_and_braces_mean_one_of()
    {
        Assert.Equal("AND(OR(word:a, word:b), word:c)", Tree("(a OR b) c"));
        Assert.Equal("OR(word:a, word:b, word:c)", Tree("{a b c}"));
        Assert.Equal("AND(OR(from:anna, from:ben), is:unread)", Tree("{from:anna from:ben} is:unread"));
        Assert.Equal("OR(AND(word:a, word:b), AND(word:c, NOT(word:d)))", Tree("(a b) OR (c -d)"));
    }

    [Fact]
    public void A_group_after_an_operator_hands_it_to_every_word_inside()
    {
        Assert.Equal("OR(from:anna, from:ben)", Tree("from:(anna OR ben)"));
        Assert.Equal("AND(subject:offer, subject:invoice)", Tree("subject:(offer invoice)"));
        Assert.Equal("NOT(AND(subject:a, subject:b))", Tree("-subject:(a b)"));
        Assert.Equal("from:Max Mustermann", Tree("from:\"Max Mustermann\""));
        Assert.Equal("OR(from:Max Mustermann, from:ben)", Tree("von:(\"Max Mustermann\" OR ben)"));
    }

    [Fact]
    public void What_is_no_operator_or_makes_no_sense_stays_a_plain_word()
    {
        Assert.Equal("word:foo:bar", Tree("foo:bar"));
        Assert.Equal("word:before:soon", Tree("before:soon"));
        Assert.Equal("word:has:wings", Tree("has:wings"));
        Assert.Equal("word:is:fancy", Tree("is:fancy"));
        Assert.Equal("word:RE:", Tree("RE:"));
        Assert.Equal("word:https://example.org/a", Tree("https://example.org/a"));
        Assert.Equal("word:larger:big", Tree("larger:big"));
        Assert.Equal("word:older_than:soon", Tree("older_than:soon"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("from:")]
    [InlineData("von: ")]
    [InlineData("()")]
    [InlineData(")(")]
    [InlineData("OR")]
    [InlineData("-")]
    [InlineData("\"\"")]
    [InlineData("{}")]
    public void Nothing_to_look_for_is_a_plain_listing(string text) => Assert.False(MailQuery.Parse(1, 5, text, Now).IsSearch, text);

    [Fact]
    public void Brackets_that_do_not_match_do_no_harm()
    {
        Assert.Equal("AND(word:a, word:b)", Tree("(a b"));
        Assert.Equal("AND(word:a, word:b)", Tree("a) b"));
        Assert.Equal("AND(word:a, word:b)", Tree("a } b"));
        Assert.Equal("word:unclosed phrase", Tree("\"unclosed phrase"));
    }

    [Fact]
    public void Relative_dates_count_back_from_now()
    {
        DateTime At(string text) => ((TermNode)MailQuery.Parse(1, null, text, Now).Root!).Date!.Value;

        Assert.Equal(Now.AddDays(-2), At("older_than:2d"));
        Assert.Equal(Now.AddDays(-7), At("older_than:1w"));
        Assert.Equal(Now.AddMonths(-3), At("newer_than:3m"));
        Assert.Equal(Now.AddYears(-1), At("newer_than:1y"));
        Assert.Equal(Now.AddDays(-5), At("older_than:5"));   // a bare number is days
        Assert.Equal(Now.AddDays(-2), At("älter_als:2D"));
    }

    [Fact]
    public void Sizes_know_k_m_and_g()
    {
        long At(string text) => ((TermNode)MailQuery.Parse(1, null, text, Now).Root!).Number!.Value;

        Assert.Equal(500 * 1024, At("larger:500k"));
        Assert.Equal(5 * 1024 * 1024, At("larger:5M"));
        Assert.Equal(5 * 1024 * 1024, At("größer:5mb"));
        Assert.Equal(1536L * 1024 * 1024, At("larger:1.5g"));
        Assert.Equal(1_500_000, At("smaller:1500000"));
        Assert.Equal((long)(2.5 * 1024 * 1024), At("smaller:2,5M"));
    }

    [Fact]
    public void A_very_long_text_is_cut_and_stays_manageable()
    {
        MailQuery query = MailQuery.Parse(1, null, string.Join(" ", Enumerable.Repeat("word", 5000)), Now);

        Assert.True(query.IsSearch);
        Assert.True(query.Describe().Split("word:word").Length - 1 <= 120);
    }
}

/// <summary>The search against real rows: who is found for which text.</summary>
public class MailQueryDatabaseTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;
    private long _inbox;
    private long _customerFolder;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();

        using IServiceScope scope = _host.Scope();
        var folders = scope.ServiceProvider.GetRequiredService<FolderService>();
        var store = scope.ServiceProvider.GetRequiredService<MailStore>();
        long mailbox = _seed.AliceMailbox.Id;

        _inbox = (await folders.FindByKindAsync(mailbox, FolderKind.Inbox))!.Id;
        long sent = (await folders.FindByKindAsync(mailbox, FolderKind.Sent))!.Id;
        long trash = (await folders.FindByKindAsync(mailbox, FolderKind.Trash))!.Id;
        _customerFolder = (await folders.CreateAsync(mailbox, "Customers/Meier")).Folder!.Id;
        DateTime now = DateTime.UtcNow;

        // 1  from Anna, with a big attachment and "pricelist" only in the text, unread
        await store.AddAsync(_inbox, new NewMessage(WithAttachment("Anna Berger", "anna@partner.test", "Offer for hosting", "the pricelist is attached", 200_000)) { ReceivedDate = now.AddDays(-2) });
        // 2  from Ben, read and starred
        await store.AddAsync(_inbox, new NewMessage(Plain("Ben Becker", "ben@shop.test", "Invoice 2026-10", "please pay")) { ReceivedDate = now.AddDays(-5), IsRead = true, IsStarred = true });
        // 3  from Anna, a year ago, unread
        await store.AddAsync(_inbox, new NewMessage(Plain("Anna Berger", "anna@partner.test", "Lunch", "Tuesday?")) { ReceivedDate = now.AddDays(-400) });
        // 4  sent by Alice, answered
        await store.AddAsync(sent, new NewMessage(Plain("Alice", "alice@example.test", "Re: Offer for hosting", "thank you", to: "anna@partner.test")) { ReceivedDate = now.AddDays(-1), IsRead = true, IsAnswered = true });
        // 5  in the trash
        await store.AddAsync(trash, new NewMessage(Plain("Ben Becker", "ben@shop.test", "Old invoice", "forget it")) { ReceivedDate = now.AddDays(-10), IsRead = true });
        // 6  in a customer folder
        await store.AddAsync(_customerFolder, new NewMessage(Plain("Meier GmbH", "info@meier.test", "Contract", "signed")) { ReceivedDate = now.AddDays(-20), IsRead = true });
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static byte[] Plain(string name, string address, string subject, string body, string? to = null)
        => RawMail.Build($"{name} <{address}>", to ?? "alice@example.test", subject, body);

    private static byte[] WithAttachment(string name, string address, string subject, string body, int bytes)
    {
        var builder = new BodyBuilder { TextBody = body };
        builder.Attachments.Add("list.pdf", new byte[bytes], ContentType.Parse("application/pdf"));
        var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private async Task<string[]> FindAsync(string text, long? folderId = null)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailQuery query = MailQuery.Parse(_seed.AliceMailbox.Id, folderId, text);
        await query.ResolveFoldersAsync(db);
        return await query.Apply(db.MailMessages.AsNoTracking(), db).Select(m => m.Subject).OrderBy(s => s).ToArrayAsync();
    }

    [DbFact]
    public async Task Without_a_text_the_whole_mailbox_is_listed_except_trash_and_spam()
        => Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch", "Offer for hosting", "Re: Offer for hosting" }, await FindAsync(""));

    [DbFact]
    public async Task A_folder_limits_the_listing_to_itself()
    {
        Assert.Equal(new[] { "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("", _inbox));
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("from:anna", _inbox));
    }

    [DbFact]
    public async Task An_in_in_the_text_wins_over_the_folder_of_the_list()
    {
        Assert.Equal(new[] { "Re: Offer for hosting" }, await FindAsync("in:sent", _inbox));
        Assert.Equal(new[] { "Invoice 2026-10", "Old invoice" }, await FindAsync("in:anywhere invoice", _inbox));
        Assert.Equal(new[] { "Invoice 2026-10" }, await FindAsync("-in:sent invoice", _inbox));   // a "not in" does not name a folder: the list stays what it is
    }

    [DbFact]
    public async Task Words_look_in_the_subject_the_people_and_the_text()
    {
        Assert.Equal(new[] { "Offer for hosting", "Re: Offer for hosting" }, await FindAsync("offer"));
        Assert.Equal(new[] { "Offer for hosting", "Re: Offer for hosting" }, await FindAsync("\"for hosting\""));
        Assert.Equal(new[] { "Offer for hosting" }, await FindAsync("pricelist"));   // only in the text
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("berger"));
    }

    [DbFact]
    public async Task From_to_and_subject_with_operators_in_both_languages()
    {
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("from:anna"));
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("von:partner.test"));
        Assert.Equal(new[] { "Lunch" }, await FindAsync("from:anna lunch"));
        Assert.Equal(new[] { "Re: Offer for hosting" }, await FindAsync("to:anna"));
        Assert.Equal(new[] { "Invoice 2026-10" }, await FindAsync("betreff:invoice"));
    }

    [DbFact]
    public async Task Or_and_groups()
    {
        Assert.Equal(new[] { "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("from:anna OR from:ben"));
        Assert.Equal(new[] { "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("from:(anna OR ben)"));
        Assert.Equal(new[] { "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("{from:anna from:ben}"));
        Assert.Equal(new[] { "Lunch" }, await FindAsync("(from:anna OR from:ben) is:unread -offer"));
    }

    [DbFact]
    public async Task A_minus_leaves_out()
    {
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Re: Offer for hosting" }, await FindAsync("-from:anna"));
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("-subject:re:"));   // the sent answer starts with "Re:"
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch", "Re: Offer for hosting" }, await FindAsync("-pricelist"));
    }

    [DbFact]
    public async Task Flags_and_attachments()
    {
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("is:unread"));
        Assert.Equal(new[] { "Lunch", "Offer for hosting" }, await FindAsync("ist:ungelesen"));
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Re: Offer for hosting" }, await FindAsync("is:read"));
        Assert.Equal(new[] { "Invoice 2026-10" }, await FindAsync("is:starred"));
        Assert.Equal(new[] { "Re: Offer for hosting" }, await FindAsync("is:answered"));
        Assert.Equal(new[] { "Offer for hosting" }, await FindAsync("has:attachment"));
        Assert.Equal(new[] { "Offer for hosting" }, await FindAsync("hat:anhang"));
    }

    [DbFact]
    public async Task In_names_a_folder_and_anywhere_includes_trash_and_spam()
    {
        Assert.Equal(new[] { "Re: Offer for hosting" }, await FindAsync("in:sent"));
        Assert.Equal(new[] { "Re: Offer for hosting" }, await FindAsync("in:gesendet"));
        Assert.Equal(new[] { "Old invoice" }, await FindAsync("in:trash"));
        Assert.Equal(new[] { "Invoice 2026-10", "Old invoice" }, await FindAsync("in:anywhere invoice"));
        Assert.Equal(new[] { "Contract" }, await FindAsync("in:Customers/Meier"));
        Assert.Equal(new[] { "Contract" }, await FindAsync("in:meier"));   // the name is enough
        Assert.Equal(new[] { "Contract" }, await FindAsync("in:\"customers/meier\" signed"));
        Assert.Empty(await FindAsync("in:nothing-like-that"));
        Assert.Equal(new[] { "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("in:inbox"));
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch", "Offer for hosting" }, await FindAsync("-in:sent"));   // and still without the trash
    }

    [DbFact]
    public async Task Dates_and_sizes()
    {
        Assert.Equal(new[] { "Lunch" }, await FindAsync("older_than:1y"));
        Assert.Equal(new[] { "Offer for hosting", "Re: Offer for hosting" }, await FindAsync("newer_than:3d"));
        Assert.Equal(new[] { "Contract", "Invoice 2026-10" }, await FindAsync("newer_than:1y older_than:3d is:read -in:sent"));
        string cutoff = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch" }, await FindAsync($"before:{cutoff}"));
        Assert.Equal(new[] { "Offer for hosting", "Re: Offer for hosting" }, await FindAsync($"after:{cutoff}"));
        Assert.Equal(new[] { "Offer for hosting" }, await FindAsync("larger:100k"));
        Assert.Equal(new[] { "Contract", "Invoice 2026-10", "Lunch", "Re: Offer for hosting" }, await FindAsync("smaller:100k"));
    }
}

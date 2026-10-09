using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

/// <summary>The order of a folder tree, without a database.</summary>
public class FolderArrangeTests
{
    private static MailFolder Folder(long id, string name, long? parent = null, FolderKind kind = FolderKind.Custom)
        => new() { Id = id, Name = name, ParentId = parent, Kind = kind };

    [Fact]
    public void Every_folder_is_followed_by_its_subfolders()
    {
        var folders = new[]
        {
            Folder(1, "INBOX", kind: FolderKind.Inbox), Folder(2, "Drafts", kind: FolderKind.Drafts), Folder(3, "Sent", kind: FolderKind.Sent),
            Folder(4, "Archive", kind: FolderKind.Archive), Folder(5, "Junk", kind: FolderKind.Junk), Folder(6, "Trash", kind: FolderKind.Trash),
            Folder(7, "Zeta"), Folder(8, "Alpha"), Folder(9, "Clients", 1), Folder(10, "2026", 9), Folder(11, "Old", 4), Folder(12, "beta", 8),
        };

        IReadOnlyList<FolderInfo> tree = FolderService.Arrange(folders);

        Assert.Equal(
            new[] { "INBOX", "INBOX/Clients", "INBOX/Clients/2026", "Drafts", "Sent", "Archive", "Archive/Old", "Junk", "Trash", "Alpha", "Alpha/beta", "Zeta" },
            tree.Select(f => f.Path));
    }

    [Fact]
    public void A_folder_whose_parent_is_missing_is_shown_at_the_top_level_and_a_cycle_ends()
    {
        IReadOnlyList<FolderInfo> tree = FolderService.Arrange(new[] { Folder(1, "Lost", 99), Folder(2, "A", 3), Folder(3, "B", 2) });

        Assert.Equal(new[] { "Lost" }, tree.Select(f => f.Path));   // the cycle has no root and is not listed: nothing loops
    }
}

/// <summary>Subfolders: creating below a folder, moving folders, and what must not be possible.</summary>
public class FolderTreeTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> WithFoldersAsync<T>(Func<FolderService, Task<T>> action)
    {
        using IServiceScope scope = _host.Scope();
        return await action(scope.ServiceProvider.GetRequiredService<FolderService>());
    }

    private async Task<string[]> PathsAsync() => (await WithFoldersAsync(f => f.ListAsync(_seed.AliceMailbox.Id))).Select(f => f.Path).ToArray();

    private async Task<MailFolder> CreateAsync(string path)
    {
        (MailFolder? folder, string? error) = await WithFoldersAsync(f => f.CreateAsync(_seed.AliceMailbox.Id, path));
        Assert.True(folder is not null, error);
        return folder;
    }

    [DbFact]
    public async Task A_folder_can_be_created_below_the_inbox_or_any_other_folder()
    {
        MailFolder inbox = (await WithFoldersAsync(f => f.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox)))!;
        (MailFolder? clients, string? error) = await WithFoldersAsync(f => f.CreateUnderAsync(_seed.AliceMailbox.Id, inbox.Id, "Clients"));
        Assert.Null(error);
        (MailFolder? year, _) = await WithFoldersAsync(f => f.CreateUnderAsync(_seed.AliceMailbox.Id, clients!.Id, "2026/Q1"));   // a path creates the levels in between

        Assert.Equal(inbox.Id, clients!.ParentId);
        Assert.NotNull(year);
        Assert.Equal(new[] { "INBOX", "INBOX/Clients", "INBOX/Clients/2026", "INBOX/Clients/2026/Q1", "Drafts", "Sent", "Archive", "Junk", "Trash" }, await PathsAsync());
    }

    [DbFact]
    public async Task A_subfolder_of_another_mailbox_cannot_be_made_with_a_foreign_parent()
    {
        MailFolder bobInbox = (await WithFoldersAsync(f => f.FindByKindAsync(_seed.BobMailbox.Id, FolderKind.Inbox)))!;

        (MailFolder? folder, string? error) = await WithFoldersAsync(f => f.CreateUnderAsync(_seed.AliceMailbox.Id, bobInbox.Id, "Sneaky"));

        Assert.Null(folder);
        Assert.Equal("The folder does not exist.", error);
    }

    [DbFact]
    public async Task A_folder_moves_below_another_one_with_everything_below_it_and_back_to_the_top()
    {
        MailFolder projects = await CreateAsync("Projects");
        MailFolder alpha = await CreateAsync("Alpha");
        await CreateAsync("Alpha/Docs");
        MailFolder inbox = (await WithFoldersAsync(f => f.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Inbox)))!;

        Assert.Null(await WithFoldersAsync(f => f.MoveAsync(alpha.Id, projects.Id)));
        Assert.Contains("Projects/Alpha/Docs", await PathsAsync());

        Assert.Null(await WithFoldersAsync(f => f.MoveAsync(alpha.Id, inbox.Id)));          // below a system folder
        Assert.Contains("INBOX/Alpha/Docs", await PathsAsync());

        Assert.Null(await WithFoldersAsync(f => f.MoveAsync(alpha.Id, null)));              // and back to the top
        Assert.Contains("Alpha/Docs", await PathsAsync());
        Assert.Null(await WithFoldersAsync(f => f.MoveAsync(alpha.Id, null)));              // already there: nothing to do
    }

    [DbFact]
    public async Task A_folder_cannot_be_moved_into_itself_or_into_what_is_below_it()
    {
        MailFolder a = await CreateAsync("A");
        MailFolder b = await CreateAsync("A/B");
        MailFolder c = await CreateAsync("A/B/C");

        Assert.Equal("A folder cannot be moved into itself or one of its subfolders.", await WithFoldersAsync(f => f.MoveAsync(a.Id, a.Id)));
        Assert.Equal("A folder cannot be moved into itself or one of its subfolders.", await WithFoldersAsync(f => f.MoveAsync(a.Id, c.Id)));
        Assert.Equal("A folder cannot be moved into itself or one of its subfolders.", await WithFoldersAsync(f => f.MoveAsync(b.Id, c.Id)));
        Assert.Equal(new[] { "A", "A/B", "A/B/C" }, (await PathsAsync()).Where(p => p == "A" || p.StartsWith("A/")));
    }

    [DbFact]
    public async Task System_folders_stay_where_they_are()
    {
        MailFolder projects = await CreateAsync("Projects");
        MailFolder trash = (await WithFoldersAsync(f => f.FindByKindAsync(_seed.AliceMailbox.Id, FolderKind.Trash)))!;

        Assert.Equal("System folders cannot be moved.", await WithFoldersAsync(f => f.MoveAsync(trash.Id, projects.Id)));
        Assert.Equal("The folder does not exist.", await WithFoldersAsync(f => f.MoveAsync(long.MaxValue, projects.Id)));
    }

    [DbFact]
    public async Task A_name_that_is_taken_at_the_target_or_reserved_at_the_top_is_refused()
    {
        MailFolder projects = await CreateAsync("Projects");
        await CreateAsync("Projects/Docs");
        MailFolder docs = await CreateAsync("Docs");
        MailFolder sent = await CreateAsync("Projects/Sent");

        Assert.Equal("A folder with this name already exists.", await WithFoldersAsync(f => f.MoveAsync(docs.Id, projects.Id)));
        Assert.Equal("This name is reserved for a system folder.", await WithFoldersAsync(f => f.MoveAsync(sent.Id, null)));
    }

    [DbFact]
    public async Task Folders_cannot_be_nested_deeper_than_the_limit()
    {
        string deepest = string.Join('/', Enumerable.Range(1, FolderService.MaxDepth).Select(i => "L" + i));
        await CreateAsync(deepest);
        (MailFolder? tooDeep, string? error) = await WithFoldersAsync(f => f.CreateAsync(_seed.AliceMailbox.Id, deepest + "/One more"));
        Assert.Null(tooDeep);
        Assert.Equal("The folders are nested too deeply.", error);

        MailFolder loose = await CreateAsync("Loose");
        MailFolder bottom = (await WithFoldersAsync(f => f.ListAsync(_seed.AliceMailbox.Id))).Single(i => i.Path == deepest).Folder;
        Assert.Equal("The folders are nested too deeply.", await WithFoldersAsync(f => f.MoveAsync(loose.Id, bottom.Id)));
    }

    [DbFact]
    public async Task Renaming_keeps_a_folder_where_it_is_and_deleting_takes_the_subfolders_and_their_mail_along()
    {
        MailFolder clients = await CreateAsync("INBOX/Clients");
        MailFolder year = await CreateAsync("INBOX/Clients/2026");
        using (IServiceScope scope = _host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<MailStore>().AddAsync(year.Id, new NewMessage(RawMail.Build("a@x.test", "alice@example.test", "In the subfolder", "text")));
        }

        Assert.Null(await WithFoldersAsync(f => f.RenameAsync(clients.Id, "INBOX/Customers")));
        Assert.Contains("INBOX/Customers/2026", await PathsAsync());

        Assert.Null(await WithFoldersAsync(f => f.DeleteAsync(clients.Id)));
        Assert.DoesNotContain(await PathsAsync(), p => p.Contains("Customers"));
        using IServiceScope check = _host.Scope();
        Assert.Equal(0, await check.ServiceProvider.GetRequiredService<MatMailDbContext>().MailMessages.CountAsync(m => m.Subject == "In the subfolder"));
    }

    [DbFact]
    public async Task The_subtree_is_the_folder_and_everything_below()
    {
        MailFolder a = await CreateAsync("A");
        await CreateAsync("A/B");
        await CreateAsync("A/B/C");
        await CreateAsync("Other");

        IReadOnlyList<MailFolder> subtree = await WithFoldersAsync(f => f.GetSubtreeAsync(a.Id));

        Assert.Equal(new[] { "A", "B", "C" }, subtree.Select(f => f.Name).OrderBy(n => n));
    }
}

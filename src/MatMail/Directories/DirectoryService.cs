using MatMail.Data;
using MatMail.Services;

namespace MatMail.Directories;

/// <summary>
/// What a directory is asked: who is this login, may they sign in, is this their password, who is there. The mapper of a connection turns the
/// entries into <see cref="DirectoryUser"/>s; nothing here touches the users of MatMail (see <see cref="DirectoryProvisioner"/>).
/// </summary>
public sealed class DirectoryService
{
    /// <summary>How many people a test and a search look at.</summary>
    public const int TestLimit = 1000;

    private const string ActiveDirectoryNestedRule = "1.2.840.113556.1.4.1941";

    /// <summary>Active Directory: bit 2 of <c>userAccountControl</c> is "account disabled".</summary>
    private const int AccountDisabled = 2;

    private readonly IDirectoryClientFactory _clients;
    private readonly SecretProtector _secrets;

    public DirectoryService(IDirectoryClientFactory clients, SecretProtector secrets)
    {
        _clients = clients;
        _secrets = secrets;
    }

    /// <summary>How to reach the directory. <paramref name="bindPassword"/>: the one just typed on the page, not yet stored; else the stored one is used.</summary>
    public DirectorySettings SettingsOf(DirectoryConnection dir, string? bindPassword = null)
        => new(
            dir.Host.Trim(), dir.Port, dir.Security, dir.AllowInvalidCertificate,
            string.IsNullOrWhiteSpace(dir.BindDn) ? null : dir.BindDn.Trim(),
            string.IsNullOrWhiteSpace(bindPassword) ? _secrets.Unprotect(dir.BindPasswordProtected) : bindPassword);

    /// <summary>The attributes the mapper reads, and the ones the checks need.</summary>
    private static string[] AttributesOf(DirectoryConnection dir)
        => new[]
            {
                dir.LoginAttribute, dir.DisplayNameAttribute, dir.EmailAttribute, dir.FirstNameAttribute, dir.LastNameAttribute, dir.JobTitleAttribute,
                dir.PhoneAttribute, dir.MobileAttribute, dir.DepartmentAttribute, "entryUUID", "objectGUID", "userAccountControl",
            }
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // ---------------------------------------------------------------------------------------------------------------
    // The mapper
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>An entry as a person; null when it has no login (an entry the mapper cannot use).</summary>
    public static DirectoryUser? MapUser(DirectoryConnection dir, DirectoryEntry entry)
    {
        string? login = entry.First(dir.LoginAttribute);
        if (login is null)
        {
            return null;
        }

        string display = entry.First(dir.DisplayNameAttribute) ?? login;
        string? uid = entry.First("entryUUID") ?? entry.First("objectGUID");
        bool disabled = int.TryParse(entry.First("userAccountControl"), out int control) && (control & AccountDisabled) != 0;
        return new DirectoryUser(
            entry.Dn, uid, login.ToLowerInvariant(), display, entry.First(dir.EmailAttribute), entry.First(dir.FirstNameAttribute), entry.First(dir.LastNameAttribute),
            entry.First(dir.JobTitleAttribute), entry.First(dir.PhoneAttribute), entry.First(dir.MobileAttribute), entry.First(dir.DepartmentAttribute), disabled);
    }

    /// <summary>The part of a search that lets in only the members of the group, when the groups are read from the person (else empty).</summary>
    private static string? GroupFilter(DirectoryConnection dir)
    {
        if (string.IsNullOrWhiteSpace(dir.AllowedGroupDn) || dir.GroupLookup != GroupLookup.UserMemberOf)
        {
            return null;
        }

        string group = LdapFilter.Escape(dir.AllowedGroupDn.Trim());
        return dir.NestedGroups ? $"(memberOf:{ActiveDirectoryNestedRule}:={group})" : $"(memberOf={group})";
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Questions
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Looks a login up: the entry of the person, or why there is none for this sign-in.</summary>
    public async Task<DirectoryLookup> FindUserAsync(DirectoryConnection dir, string login, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return DirectoryLookup.NotFound;
        }

        string byLogin = $"({dir.LoginAttribute.Trim()}={LdapFilter.Escape(login.Trim())})";
        using IDirectoryClient client = _clients.Create(SettingsOf(dir));
        await client.ConnectAsync(cancel);

        IReadOnlyList<DirectoryEntry> found = await client.SearchAsync(dir.BaseDn, LdapFilter.And(dir.UserFilter, byLogin, GroupFilter(dir)), AttributesOf(dir), 2, cancel);
        if (found.Count == 0 && GroupFilter(dir) is not null)
        {
            // Is the person there at all? Then the group is what keeps them out (the log says so).
            found = await client.SearchAsync(dir.BaseDn, LdapFilter.And(dir.UserFilter, byLogin), AttributesOf(dir), 2, cancel);
            return found.Count == 1 && MapUser(dir, found[0]) is not null ? new DirectoryLookup(null, NotAllowed: true) : DirectoryLookup.NotFound;
        }

        // Two entries with one login: nobody can tell which is meant, so nobody signs in.
        if (found.Count != 1 || MapUser(dir, found[0]) is not { } user)
        {
            return DirectoryLookup.NotFound;
        }

        if (!await IsMemberOfGroupAsync(client, dir, user, cancel))
        {
            return new DirectoryLookup(null, NotAllowed: true);
        }

        return new DirectoryLookup(user);
    }

    /// <summary>One person by their distinguished name (what a comparison with the directory asks for); null when there is nobody, or the group does not let them in.</summary>
    public async Task<DirectoryUser?> ReadUserAsync(DirectoryConnection dir, string dn, CancellationToken cancel = default)
    {
        using IDirectoryClient client = _clients.Create(SettingsOf(dir));
        await client.ConnectAsync(cancel);
        DirectoryEntry? entry = await client.ReadAsync(dn, AttributesOf(dir), cancel);
        if (entry is null || MapUser(dir, entry) is not { } user)
        {
            return null;
        }

        return await IsMemberOfGroupAsync(client, dir, user, cancel) && await InFilterAsync(client, dir, user, cancel) ? user : null;
    }

    /// <summary>Whether the filter that says who is a person (and, with memberOf, the group) still holds for the entry.</summary>
    private static async Task<bool> InFilterAsync(IDirectoryClient client, DirectoryConnection dir, DirectoryUser user, CancellationToken cancel)
    {
        string byName = $"({dir.LoginAttribute.Trim()}={LdapFilter.Escape(user.Login)})";
        IReadOnlyList<DirectoryEntry> found = await client.SearchAsync(dir.BaseDn, LdapFilter.And(dir.UserFilter, byName, GroupFilter(dir)), [dir.LoginAttribute], 2, cancel);
        return found.Any(e => LdapFilter.SameDn(e.Dn, user.Dn));
    }

    /// <summary>With the groups read from the member list of the group (not from the person) the list is looked at here.</summary>
    private static async Task<bool> IsMemberOfGroupAsync(IDirectoryClient client, DirectoryConnection dir, DirectoryUser user, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(dir.AllowedGroupDn) || dir.GroupLookup != GroupLookup.GroupMembers)
        {
            return true;
        }

        DirectoryEntry? group = await client.ReadAsync(dir.AllowedGroupDn.Trim(), ["member", "uniqueMember"], cancel);
        return group is not null && group.All("member").Concat(group.All("uniqueMember")).Any(m => LdapFilter.SameDn(m, user.Dn));
    }

    /// <summary>Is this the password of the person? (A bind as the person; the account that searches is not involved.)</summary>
    public Task<bool> VerifyPasswordAsync(DirectoryConnection dir, string dn, string password, CancellationToken cancel = default)
        => string.IsNullOrEmpty(password) ? Task.FromResult(false) : _clients.CheckPasswordAsync(SettingsOf(dir), dn, password, cancel);

    /// <summary>The people of the directory (those who may sign in), by a word in their login, name or address; the most <paramref name="limit"/> of them.</summary>
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(DirectoryConnection dir, string? text, int limit, CancellationToken cancel = default)
    {
        string? words = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            string like = $"*{LdapFilter.Escape(text.Trim())}*";
            words = "(|" + string.Concat(new[] { dir.LoginAttribute, dir.DisplayNameAttribute, dir.EmailAttribute }
                .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => $"({a!.Trim()}={like})")) + ")";
        }

        using IDirectoryClient client = _clients.Create(SettingsOf(dir));
        await client.ConnectAsync(cancel);
        IReadOnlyList<DirectoryEntry> entries = await client.SearchAsync(dir.BaseDn, LdapFilter.And(dir.UserFilter, words, GroupFilter(dir)), AttributesOf(dir), limit, cancel);

        var people = new List<DirectoryUser>();
        foreach (DirectoryEntry entry in entries)
        {
            if (MapUser(dir, entry) is { } user && await IsMemberOfGroupAsync(client, dir, user, cancel))
            {
                people.Add(user);
            }
        }

        return people.OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Tries the connection the way it will be used and says what it found, for the page that edits it.</summary>
    public async Task<DirectoryCheck> TestAsync(DirectoryConnection dir, string? bindPassword = null, CancellationToken cancel = default)
    {
        var notes = new List<string>();
        try
        {
            using IDirectoryClient client = _clients.Create(SettingsOf(dir, bindPassword));
            await client.ConnectAsync(cancel);

            IReadOnlyList<DirectoryEntry> entries = await client.SearchAsync(dir.BaseDn, LdapFilter.And(dir.UserFilter, GroupFilter(dir)), AttributesOf(dir), TestLimit, cancel);
            var people = new List<DirectoryUser>();
            int withoutLogin = 0;
            foreach (DirectoryEntry entry in entries)
            {
                if (MapUser(dir, entry) is not { } user)
                {
                    withoutLogin++;
                }
                else if (await IsMemberOfGroupAsync(client, dir, user, cancel))
                {
                    people.Add(user);
                }
            }

            if (withoutLogin > 0)
            {
                notes.Add($"{withoutLogin} entries have no value in “{dir.LoginAttribute}” and are left out.");
            }

            if (entries.Count >= TestLimit)
            {
                notes.Add($"There are at least {TestLimit} entries; the test looked at the first {TestLimit}.");
            }

            if (!string.IsNullOrWhiteSpace(dir.AllowedGroupDn))
            {
                DirectoryEntry? group = await client.ReadAsync(dir.AllowedGroupDn.Trim(), ["cn"], cancel);
                notes.Add(group is null ? $"The group “{dir.AllowedGroupDn}” was not found." : $"The group “{dir.AllowedGroupDn}” exists.");
            }

            if (people.Count == 0)
            {
                notes.Add("No person was found: check the base, the filter and the login attribute" + (string.IsNullOrWhiteSpace(dir.AllowedGroupDn) ? "." : " and the group."));
            }

            string message = $"Connected to {dir.Host}:{dir.Port}" + (string.IsNullOrWhiteSpace(dir.BindDn) ? " (anonymously)" : $" as {dir.BindDn}") + $": {people.Count} people can sign in.";
            return new DirectoryCheck(people.Count > 0, message, people.Count, people.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(5).ToList(), notes);
        }
        catch (DirectoryException ex)
        {
            return new DirectoryCheck(false, ex.Message, 0, [], notes);
        }
    }
}

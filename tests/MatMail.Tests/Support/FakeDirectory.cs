using MatMail.Data;
using MatMail.Directories;

namespace MatMail.Tests.Support;

/// <summary>
/// A directory in memory: entries with attributes and passwords, searched with the small part of LDAP filters that MatMail uses
/// (<c>(&amp;…)</c>, <c>(|…)</c>, <c>(!…)</c>, <c>(attribute=value)</c> with <c>*</c> as wildcard, and the in-chain rule of Active Directory for memberOf).
/// Counts the questions, so that a test can see what was asked.
/// </summary>
public sealed class FakeDirectory : IDirectoryClientFactory
{
    public List<DirectoryEntry> Entries { get; } = [];

    /// <summary>Distinguished name → password.</summary>
    public Dictionary<string, string> Passwords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory cannot be reached.</summary>
    public bool Down { get; set; }

    public int Searches { get; private set; }
    public int Binds { get; private set; }

    public static string People => "ou=people,dc=example,dc=test";

    /// <summary>A person: <c>uid</c>, <c>cn</c>, <c>mail</c>, <c>sn</c>, <c>givenName</c> and the groups they are in (<c>memberOf</c>).</summary>
    public DirectoryEntry AddPerson(string uid, string name, string? mail = null, string? password = null, string[]? groups = null, string? uuid = null, int? control = null, string? phone = null)
    {
        string dn = $"cn={name},{People}";
        var attributes = new List<(string, string[])>
        {
            ("objectClass", ["inetOrgPerson", "person"]),
            ("uid", [uid]),
            ("cn", [name]),
            ("sn", [name.Split(' ').Last()]),
            ("givenName", [name.Split(' ').First()]),
            ("entryUUID", [uuid ?? Guid.NewGuid().ToString()]),
        };
        if (mail is not null)
        {
            attributes.Add(("mail", [mail]));
        }

        if (groups is { Length: > 0 })
        {
            attributes.Add(("memberOf", groups));
        }

        if (control is not null)
        {
            attributes.Add(("userAccountControl", [control.Value.ToString()]));
        }

        if (phone is not null)
        {
            attributes.Add(("telephoneNumber", [phone]));
        }

        DirectoryEntry entry = DirectoryEntry.Of(dn, attributes.ToArray());
        Entries.Add(entry);
        Passwords[dn] = password ?? uid;
        return entry;
    }

    /// <summary>A group with its members (a list of the distinguished names).</summary>
    public DirectoryEntry AddGroup(string name, params string[] members)
    {
        DirectoryEntry entry = DirectoryEntry.Of($"cn={name},{People}", ("objectClass", ["groupOfNames"]), ("cn", [name]), ("member", members));
        Entries.Add(entry);
        return entry;
    }

    public void Remove(DirectoryEntry entry) => Entries.RemoveAll(e => e.Dn == entry.Dn);

    public void Replace(DirectoryEntry old, DirectoryEntry replacement)
    {
        int index = Entries.FindIndex(e => e.Dn == old.Dn);
        Entries[index] = replacement;
        if (Passwords.Remove(old.Dn, out string? password))
        {
            Passwords[replacement.Dn] = password;
        }
    }

    public IDirectoryClient Create(DirectorySettings settings) => new Client(this, settings);

    public Task<bool> CheckPasswordAsync(DirectorySettings settings, string dn, string password, CancellationToken cancel)
    {
        if (Down)
        {
            throw new DirectoryException("No connection to the directory.");
        }

        Binds++;
        return Task.FromResult(!string.IsNullOrEmpty(password) && Passwords.TryGetValue(dn, out string? expected) && expected == password);
    }

    private sealed class Client(FakeDirectory directory, DirectorySettings settings) : IDirectoryClient
    {
        public Task ConnectAsync(CancellationToken cancel)
        {
            if (directory.Down)
            {
                throw new DirectoryException($"No connection to {settings.Host}:{settings.Port}.");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string baseDn, string filter, IReadOnlyCollection<string> attributes, int limit, CancellationToken cancel)
        {
            directory.Searches++;
            IReadOnlyList<DirectoryEntry> found = directory.Entries
                .Where(e => e.Dn.EndsWith(baseDn.Trim(), StringComparison.OrdinalIgnoreCase) && Matches(e, filter.Trim()))
                .Take(limit)
                .ToList();
            return Task.FromResult(found);
        }

        public Task<DirectoryEntry?> ReadAsync(string dn, IReadOnlyCollection<string> attributes, CancellationToken cancel)
        {
            directory.Searches++;
            return Task.FromResult(directory.Entries.FirstOrDefault(e => LdapFilter.SameDn(e.Dn, dn)));
        }

        public void Dispose()
        {
        }
    }

    // ---- a filter, evaluated ---------------------------------------------------------------------------------------------

    private static bool Matches(DirectoryEntry entry, string filter)
    {
        if (!filter.StartsWith('(') || !filter.EndsWith(')'))
        {
            throw new FormatException("Not a filter in brackets: " + filter);
        }

        string inner = filter[1..^1];
        switch (inner[0])
        {
            case '&':
                return Parts(inner[1..]).All(p => Matches(entry, p));
            case '|':
                return Parts(inner[1..]).Any(p => Matches(entry, p));
            case '!':
                return !Matches(entry, inner[1..]);
        }

        int equals = inner.IndexOf('=');
        string attribute = inner[..equals];
        string raw = inner[(equals + 1)..];

        // Active Directory: the groups of a person, also through other groups – this fake has no nesting, so it is the plain memberOf.
        int rule = attribute.IndexOf(':');
        if (rule >= 0)
        {
            attribute = attribute[..rule];
            raw = raw.TrimStart('=');
        }

        IEnumerable<string> values = entry.All(attribute);
        if (attribute.Equals("objectClass", StringComparison.OrdinalIgnoreCase) && raw == "*")
        {
            return true;
        }

        // An asterisk that stands as it is is a wildcard; an escaped one (backslash, 2a) is one of the value: what is unescaped is compared as it is.
        string[] pieces = raw.Split('*').Select(Unescape).ToArray();
        return pieces.Length > 1
            ? values.Any(v => Like(v, pieces))
            : values.Any(v => string.Equals(v, pieces[0], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The value has the pieces in this order, the first at its start (when the pattern did not begin with a wildcard) and the last at its end.</summary>
    private static bool Like(string value, string[] pieces)
    {
        int at = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Length == 0)
            {
                continue;
            }

            int found = value.IndexOf(pieces[i], at, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || (i == 0 && found != 0))
            {
                return false;
            }

            at = found + pieces[i].Length;
        }

        return pieces[^1].Length == 0 || at == value.Length;
    }

    private static IEnumerable<string> Parts(string text)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i += 2;
            }
            else if (text[i] == '(')
            {
                if (depth++ == 0)
                {
                    start = i;
                }
            }
            else if (text[i] == ')' && --depth == 0)
            {
                yield return text[start..(i + 1)];
            }
        }
    }

    private static string Unescape(string value)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 2 < value.Length + 0 && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(System.Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}

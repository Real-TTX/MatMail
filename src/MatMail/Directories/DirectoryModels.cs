using MatMail.Data;

namespace MatMail.Directories;

/// <summary>The directory could not be used: not reached, refused the account, did not understand the search. The text says what; it is safe to show.</summary>
public sealed class DirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An entry of a directory: its distinguished name and the attributes that were asked for (names compare without regard to case).</summary>
public sealed record DirectoryEntry(string Dn, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes)
{
    /// <summary>The first value of an attribute that says something; null when there is none or no attribute was named.</summary>
    public string? First(string? attribute)
        => string.IsNullOrWhiteSpace(attribute) || !Attributes.TryGetValue(attribute.Trim(), out IReadOnlyList<string>? values)
            ? null
            : values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0);

    public IReadOnlyList<string> All(string attribute)
        => Attributes.TryGetValue(attribute, out IReadOnlyList<string>? values) ? values : [];

    public static DirectoryEntry Of(string dn, params (string Attribute, string[] Values)[] attributes)
        => new(dn, attributes.ToDictionary(a => a.Attribute, a => (IReadOnlyList<string>)a.Values, StringComparer.OrdinalIgnoreCase));
}

/// <summary>How to reach a directory: what a client needs, without the entity (and with the password in the clear).</summary>
public sealed record DirectorySettings(
    string Host, int Port, DirectorySecurity Security, bool AllowInvalidCertificate, string? BindDn, string? BindPassword, int TimeoutSeconds = 15);

/// <summary>A person of a directory as the mapper of a connection sees them.</summary>
public sealed record DirectoryUser(
    string Dn, string? Uid, string Login, string DisplayName, string? Email, string? FirstName, string? LastName, string? JobTitle, string? Phone, string? Mobile,
    string? Department, bool Disabled);

/// <summary>
/// What a search for a login found: nobody (<see cref="User"/> and <see cref="NotAllowed"/> both empty), somebody the group does not let in
/// (<see cref="NotAllowed"/>, so that the log can say why), or somebody.
/// </summary>
public sealed record DirectoryLookup(DirectoryUser? User, bool NotAllowed = false)
{
    public static readonly DirectoryLookup NotFound = new(null, false);
}

/// <summary>The outcome of the test of a connection (what the page shows).</summary>
public sealed record DirectoryCheck(bool Ok, string Message, int Users, IReadOnlyList<DirectoryUser> Sample, IReadOnlyList<string> Notes);

/// <summary>A client for one directory; <see cref="IDirectoryClientFactory"/> makes them.</summary>
public interface IDirectoryClient : IDisposable
{
    /// <summary>Connects (with TLS as configured) and signs in as the account that searches; anonymous without one.</summary>
    Task ConnectAsync(CancellationToken cancel);

    /// <summary>The entries below <paramref name="baseDn"/> that match the filter, at most <paramref name="limit"/> of them.</summary>
    Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string baseDn, string filter, IReadOnlyCollection<string> attributes, int limit, CancellationToken cancel);

    /// <summary>One entry by its name; null when it is not there.</summary>
    Task<DirectoryEntry?> ReadAsync(string dn, IReadOnlyCollection<string> attributes, CancellationToken cancel);
}

public interface IDirectoryClientFactory
{
    IDirectoryClient Create(DirectorySettings settings);

    /// <summary>
    /// Whether a password is the one of an entry: a connection of its own that signs in as the entry (never the one of the account that
    /// searches). An empty password is never accepted: many servers take a bind without one as an anonymous one and say "success".
    /// </summary>
    Task<bool> CheckPasswordAsync(DirectorySettings settings, string dn, string password, CancellationToken cancel);
}

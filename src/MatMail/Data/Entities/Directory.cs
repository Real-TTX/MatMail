namespace MatMail.Data;

/// <summary>How the connection to a directory is protected.</summary>
public enum DirectorySecurity
{
    /// <summary>Plain LDAP (port 389). Passwords travel in the clear: only for a network that nobody can listen to.</summary>
    None,

    /// <summary>LDAP on port 389, switched to TLS with STARTTLS before anything is sent.</summary>
    StartTls,

    /// <summary>LDAP over TLS (LDAPS, port 636).</summary>
    Ldaps,
}

/// <summary>Where the groups of a person are read from.</summary>
public enum GroupLookup
{
    /// <summary>The person's own <c>memberOf</c> attribute (Active Directory, OpenLDAP with the memberOf overlay).</summary>
    UserMemberOf,

    /// <summary>The member list of the group (<c>member</c> or <c>uniqueMember</c>).</summary>
    GroupMembers,
}

/// <summary>
/// A directory (Active Directory or another LDAP server) that a tenant signs its people in with: how to reach it, which of its entries are
/// people, how their attributes become the fields of a user (the mapper), and who of them may sign in.
/// </summary>
public class DirectoryConnection : AuditedEntity, ITenantEntity
{
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // ----- Connection -----
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 389;
    public DirectorySecurity Security { get; set; } = DirectorySecurity.StartTls;

    /// <summary>Accept a certificate that does not chain to a trusted one (a self-signed one of the directory).</summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>The account that searches the directory (a distinguished name, or for Active Directory also a user principal name). Empty: anonymous.</summary>
    public string? BindDn { get; set; }

    /// <summary>The password of <see cref="BindDn"/>, protected with the key ring of the server.</summary>
    public string? BindPasswordProtected { get; set; }

    // ----- Who is a person -----
    /// <summary>Where the search starts (e.g. <c>ou=people,dc=example,dc=com</c>); the whole tree below it is searched.</summary>
    public string BaseDn { get; set; } = string.Empty;

    /// <summary>Which entries are people, an LDAP filter (e.g. <c>(objectClass=person)</c>, for Active Directory <c>(&amp;(objectCategory=person)(objectClass=user))</c>).</summary>
    public string UserFilter { get; set; } = "(objectClass=person)";

    /// <summary>The attribute the login name is taken from and searched by (<c>uid</c>; Active Directory: <c>sAMAccountName</c> or <c>userPrincipalName</c>).</summary>
    public string LoginAttribute { get; set; } = "uid";

    // ----- The mapper: attributes of an entry -> fields of a user (empty: the field is not filled from the directory) -----
    public string DisplayNameAttribute { get; set; } = "cn";
    public string? EmailAttribute { get; set; } = "mail";
    public string? FirstNameAttribute { get; set; } = "givenName";
    public string? LastNameAttribute { get; set; } = "sn";
    public string? JobTitleAttribute { get; set; } = "title";
    public string? PhoneAttribute { get; set; } = "telephoneNumber";
    public string? MobileAttribute { get; set; } = "mobile";
    public string? DepartmentAttribute { get; set; } = "department";

    // ----- Who may sign in -----
    /// <summary>The distinguished name of a group whose members may sign in. Empty: everybody the filter finds.</summary>
    public string? AllowedGroupDn { get; set; }

    public GroupLookup GroupLookup { get; set; } = GroupLookup.UserMemberOf;

    /// <summary>Members of groups inside the group count too (Active Directory: the in-chain matching rule; only with <see cref="GroupLookup.UserMemberOf"/>).</summary>
    public bool NestedGroups { get; set; }

    // ----- What a person gets who signs in for the first time -----
    /// <summary>A person that the directory lets in and that has no user yet gets one when they sign in.</summary>
    public bool CreateUsersOnSignIn { get; set; } = true;

    /// <summary>The roles of such a user.</summary>
    public long[] DefaultRoleIds { get; set; } = [];

    /// <summary>Such a user gets a personal mailbox, with the address of the directory when it is one of the tenant's domains.</summary>
    public bool CreateMailbox { get; set; } = true;

    // ----- State -----
    public DateTime? LastCheckDate { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckMessage { get; set; }

    /// <summary>When the users of this directory were last compared with it, whether that worked (false: the directory could not be asked), and what came of it.</summary>
    public DateTime? LastSyncDate { get; set; }
    public bool? LastSyncOk { get; set; }
    public string? LastSyncMessage { get; set; }
}

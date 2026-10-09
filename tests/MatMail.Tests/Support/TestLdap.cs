using MatMail.Data;
using MatMail.Directories;

namespace MatMail.Tests.Support;

/// <summary>
/// Tests against a real LDAP server read <c>MATMAIL_TEST_LDAP</c>: <c>host:port[;ldaps=port]</c>. They are made for the image
/// <c>rroemhild/test-openldap</c> (OpenLDAP with the Planet Express staff: base <c>dc=planetexpress,dc=com</c>, search account
/// <c>cn=admin,dc=planetexpress,dc=com</c> / <c>GoodNewsEveryone</c>, people with <c>uid</c> and the password of their uid, the groups
/// <c>ship_crew</c> and <c>admin_staff</c>, the memberOf overlay; LDAP on 10389, LDAPS with its own certificate on 10636). Without the
/// variable the tests are skipped. In the CI it is a service container; locally:
/// <code>
/// docker run -d --name ci-ldap --network container:ci-holder rroemhild/test-openldap
/// MATMAIL_TEST_LDAP=localhost:10389;ldaps=10636
/// </code>
/// </summary>
public static class TestLdap
{
    public const string BaseDn = "dc=planetexpress,dc=com";
    public const string People = "ou=people,dc=planetexpress,dc=com";
    public const string AdminDn = "cn=admin,dc=planetexpress,dc=com";
    public const string AdminPassword = "GoodNewsEveryone";
    public const string ShipCrew = "cn=ship_crew,ou=people,dc=planetexpress,dc=com";
    public const string AdminStaff = "cn=admin_staff,ou=people,dc=planetexpress,dc=com";

    public static string? Setting => Environment.GetEnvironmentVariable("MATMAIL_TEST_LDAP");

    public static bool Available => !string.IsNullOrWhiteSpace(Setting);

    private static string[] Parts => Setting!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Host => Parts[0].Split(':')[0];
    public static int Port => int.Parse(Parts[0].Split(':')[1]);
    public static int LdapsPort => int.Parse(Parts.FirstOrDefault(p => p.StartsWith("ldaps=", StringComparison.OrdinalIgnoreCase))?[6..] ?? "10636");

    /// <summary>A connection as the page would store it (plain LDAP: the test server's STARTTLS has a certificate nobody trusts).</summary>
    public static DirectoryConnection Connection(Action<DirectoryConnection>? change = null)
    {
        var dir = new DirectoryConnection
        {
            Name = "Planet Express",
            Host = Host,
            Port = Port,
            Security = DirectorySecurity.None,
            BindDn = AdminDn,
            BaseDn = BaseDn,
            UserFilter = "(objectClass=inetOrgPerson)",
            LoginAttribute = "uid",
            DisplayNameAttribute = "cn",
            EmailAttribute = "mail",
            FirstNameAttribute = "givenName",
            LastNameAttribute = "sn",
            JobTitleAttribute = "employeeType",
            PhoneAttribute = null,
            MobileAttribute = null,
            DepartmentAttribute = null,
        };
        change?.Invoke(dir);
        return dir;
    }
}

public sealed class LdapFactAttribute : FactAttribute
{
    public LdapFactAttribute()
    {
        if (!TestLdap.Available)
        {
            Skip = "MATMAIL_TEST_LDAP is not set (needs an LDAP server, e.g. the container rroemhild/test-openldap).";
        }
    }
}

public sealed class LdapDbFactAttribute : FactAttribute
{
    public LdapDbFactAttribute()
    {
        if (!TestLdap.Available || !TestDatabase.Available)
        {
            Skip = "MATMAIL_TEST_LDAP and MATMAIL_TEST_DB are needed (an LDAP server and a PostgreSQL server).";
        }
    }
}

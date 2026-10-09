using MatMail.Data;

namespace MatMail.Tests.Support;

/// <summary>
/// Tests against a real Active Directory read <c>MATMAIL_TEST_AD</c>: <c>host:port[;ldaps=port]</c>. They are made for a Samba domain controller
/// (the image <c>nowsci/samba-domain</c>), which answers the way Active Directory does: <c>sAMAccountName</c>, <c>userPrincipalName</c>,
/// <c>userAccountControl</c>, <c>memberOf</c> and the in-chain rule for nested groups, <c>objectGUID</c>, referrals to the other partitions, the
/// error codes of a bind (<c>data 52e</c> wrong password, <c>data 533</c> disabled). Not part of the CI (provisioning a domain takes a minute and the
/// container is privileged), so without the variable the tests are skipped. Locally:
/// <code>
/// docker run -d --name ci-ad --privileged -h dc1.example.test -e DOMAIN=EXAMPLE.TEST -e DOMAINPASS='Adm1n-Passw0rd!' -e HOSTNAME=dc1.example.test \
///     -e DNSFORWARDER=8.8.8.8 -e NOCOMPLEXITY=true -e INSECURELDAP=true -p 1389:389 -p 1636:636 nowsci/samba-domain:latest
/// # after a minute, the people (samba-tool inside the container):
/// samba-tool user create svc-matmail 'Svc-Passw0rd!1'
/// samba-tool user create fry 'Fry-Passw0rd!1' --given-name=Philip --surname=Fry --mail-address=fry@example.test --job-title="Delivery boy" --department=Delivery --telephone-number="+49 721 1"
/// samba-tool user create leela 'Leela-Passw0rd!1' --given-name=Turanga --surname=Leela --mail-address=leela@example.test --job-title=Captain --department=Ship
/// samba-tool user create bender 'Bender-Passw0rd!1' --given-name=Bender --surname=Rodriguez --mail-address=bender@example.test
/// samba-tool user create zoidberg 'Zoidberg-Passw0rd!1' --given-name=John --surname=Zoidberg --mail-address=zoidberg@example.test
/// samba-tool user disable bender
/// samba-tool group add crew;  samba-tool group addmembers crew fry,leela,bender
/// samba-tool group add fleet; samba-tool group addmembers fleet crew      # a group inside a group
/// MATMAIL_TEST_AD=localhost:1389;ldaps=1636
/// </code>
/// </summary>
public static class TestAd
{
    public const string BaseDn = "dc=example,dc=test";
    public const string BindDn = "svc-matmail@example.test";
    public const string BindPassword = "Svc-Passw0rd!1";
    public const string Crew = "CN=crew,CN=Users,DC=example,DC=test";
    public const string Fleet = "CN=fleet,CN=Users,DC=example,DC=test";

    public static string? Setting => Environment.GetEnvironmentVariable("MATMAIL_TEST_AD");

    public static bool Available => !string.IsNullOrWhiteSpace(Setting);

    private static string[] Parts => Setting!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Host => Parts[0].Split(':')[0];
    public static int Port => int.Parse(Parts[0].Split(':')[1]);
    public static int LdapsPort => int.Parse(Parts.FirstOrDefault(p => p.StartsWith("ldaps=", StringComparison.OrdinalIgnoreCase))?[6..] ?? "636");

    /// <summary>A connection as the page would store it with the usual values for Active Directory.</summary>
    public static DirectoryConnection Connection(Action<DirectoryConnection>? change = null)
    {
        var dir = new DirectoryConnection
        {
            Name = "Example domain",
            Host = Host,
            Port = Port,
            Security = DirectorySecurity.None,
            BindDn = BindDn,
            BaseDn = BaseDn,
            UserFilter = "(&(objectCategory=person)(objectClass=user))",
            LoginAttribute = "sAMAccountName",
            DisplayNameAttribute = "displayName",
            EmailAttribute = "mail",
            FirstNameAttribute = "givenName",
            LastNameAttribute = "sn",
            JobTitleAttribute = "title",
            PhoneAttribute = "telephoneNumber",
            MobileAttribute = "mobile",
            DepartmentAttribute = "department",
        };
        change?.Invoke(dir);
        return dir;
    }
}

public sealed class AdFactAttribute : FactAttribute
{
    public AdFactAttribute()
    {
        if (!TestAd.Available)
        {
            Skip = "MATMAIL_TEST_AD is not set (needs a Samba Active Directory domain controller, see tests/MatMail.Tests/Support/TestAd.cs).";
        }
    }
}

public sealed class AdDbFactAttribute : FactAttribute
{
    public AdDbFactAttribute()
    {
        if (!TestAd.Available || !TestDatabase.Available)
        {
            Skip = "MATMAIL_TEST_AD and MATMAIL_TEST_DB are needed (a Samba Active Directory and a PostgreSQL server).";
        }
    }
}

using MatMail.Backup;

namespace MatMail.Tests.Support;

/// <summary>
/// Tests against a real SMB share read <c>MATMAIL_TEST_SMB</c>: <c>host;share=name;user=name;password=secret[;domain=name]</c>. SMBLibrary only
/// speaks to port 445, so the share must be reachable there: in the CI it is a Samba container on the runner (published on 445), locally
/// the tests run in a Linux container next to one (Windows has its own server on 445). Without the variable the tests are skipped.
/// <para>
/// Samba in Docker, and the suite next to it (Git Bash: <c>MSYS_NO_PATHCONV=1</c>):
/// <code>
/// docker network create smbtest
/// docker run -d --name matmail-test-samba --network smbtest -e ACCOUNT_backup=backup-secret -e UID_backup=1000 \
///   -e SAMBA_VOLUME_CONFIG_backups="[backups]; path=/shares/backups; valid users = backup; guest ok = no; read only = no; browseable = yes" \
///   -v matmail-test-samba-data:/shares/backups servercontainers/samba
/// MATMAIL_TEST_SMB=matmail-test-samba;share=backups;user=backup;password=backup-secret
/// </code>
/// </para>
/// </summary>
public static class TestSmb
{
    public static string? Setting => Environment.GetEnvironmentVariable("MATMAIL_TEST_SMB");

    public static bool Available => !string.IsNullOrWhiteSpace(Setting);

    public static string Host => Setting!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First(p => !p.Contains('='));

    public static string Share => Value("share") ?? "backups";
    public static string User => Value("user") ?? "backup";
    public static string Password => Value("password") ?? "backup-secret";
    public static string? Domain => Value("domain");

    private static string? Value(string key)
        => Setting!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(key.Length + 1)..])
            .FirstOrDefault();

    /// <summary>The share with a folder of its own (every test uses another, so tests do not meet each other's files).</summary>
    public static SmbTargetOptions Options(string? folder = null, string? password = null, string? user = null, string? share = null, string? host = null)
        => new(host ?? Host, share ?? Share, folder ?? "matmail-test/" + Guid.NewGuid().ToString("N")[..10], Domain, user ?? User, password ?? Password);
}

public sealed class SmbFactAttribute : FactAttribute
{
    public SmbFactAttribute()
    {
        if (!TestSmb.Available)
        {
            Skip = "MATMAIL_TEST_SMB is not set (needs an SMB share, e.g. a Samba container).";
        }
    }
}

public sealed class SmbDbFactAttribute : FactAttribute
{
    public SmbDbFactAttribute()
    {
        if (!TestSmb.Available || !TestDatabase.Available)
        {
            Skip = "MATMAIL_TEST_SMB / MATMAIL_TEST_DB are not set (needs an SMB share and a PostgreSQL server).";
        }
    }
}

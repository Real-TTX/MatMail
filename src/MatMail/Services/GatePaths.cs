namespace MatMail.Services;

/// <summary>
/// Addresses that stay open for a user who is held back until something is done: a forced password change, the set-up of a required
/// second factor. A fixed list, not "anything with a file extension": the mail API has addresses that end in a file name (attachments,
/// inline pictures), and those must stay closed.
/// </summary>
public static class GatePaths
{
    private static readonly string[] StaticPrefixes = { "/css/", "/js/", "/icons/", "/brand/", "/favicon" };

    /// <summary>The style sheets, scripts, icons, logos of a tenant and the favicon that every page needs to be drawn.</summary>
    public static bool IsStatic(string path) => StaticPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

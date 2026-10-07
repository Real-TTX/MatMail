namespace MatMail.Services;

/// <summary>Small helpers for e-mail addresses: normalisation, validation and the "*@domain" catch-all form.</summary>
public static class MailAddresses
{
    public const string CatchAllPrefix = "*@";

    /// <summary>Trims, strips "mailto:" and angle brackets, lower-cases.</summary>
    public static string Normalize(string? address)
    {
        string value = (address ?? string.Empty).Trim();
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        return value.Trim('<', '>', ' ').ToLowerInvariant();
    }

    /// <summary>Splits "local@domain". Both parts must be non-empty; the domain must contain a dot or be "localhost".</summary>
    public static bool TrySplit(string? address, out string local, out string domain)
    {
        local = domain = string.Empty;
        string value = Normalize(address);
        int at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1 || value.Contains(' '))
        {
            return false;
        }

        local = value[..at];
        domain = value[(at + 1)..];
        return IsValidDomain(domain) && local.Length <= 64 && (local == "*" || IsValidLocalPart(local));
    }

    public static bool IsValid(string? address) => TrySplit(address, out _, out _);

    public static bool IsValidDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        if (!domain.Contains('.') && domain != "localhost")
        {
            return false;
        }

        foreach (string label in domain.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsCatchAll(string address) => address.StartsWith(CatchAllPrefix, StringComparison.Ordinal);

    public static string DomainOf(string address)
    {
        int at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..].ToLowerInvariant();
    }

    public static string CatchAllOf(string domain) => CatchAllPrefix + domain.ToLowerInvariant();

    private static bool IsValidLocalPart(string local)
    {
        if (local.StartsWith('.') || local.EndsWith('.') || local.Contains(".."))
        {
            return false;
        }

        // RFC 5322 "atext" plus the dot; quoted local parts are not supported.
        return local.All(c => char.IsLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c));
    }
}

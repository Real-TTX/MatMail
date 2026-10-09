using System.Text;

namespace MatMail.Directories;

/// <summary>Building and checking LDAP search filters (RFC 4515).</summary>
public static class LdapFilter
{
    /// <summary>
    /// A value as it may stand in a filter: the characters that mean something there (<c>\ * ( )</c> and NUL) are written as a backslash and
    /// their two hex digits. What a person types as a login name must never be able to change the filter around it.
    /// </summary>
    public static string Escape(string value)
    {
        var text = new StringBuilder(value.Length + 8);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            if (b is (byte)'\\' or (byte)'*' or (byte)'(' or (byte)')' or 0)
            {
                text.Append('\\').Append(b.ToString("x2"));
            }
            else if (b < 0x80)
            {
                text.Append((char)b);
            }
            else
            {
                // a character beyond ASCII goes as its UTF-8 bytes, each as \xx
                text.Append('\\').Append(b.ToString("x2"));
            }
        }

        return text.ToString();
    }

    /// <summary>The filter that holds when all the given filters hold; empty ones are left out.</summary>
    public static string And(params string?[] filters)
    {
        string[] parts = filters.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Normalize(f!)).ToArray();
        return parts.Length switch
        {
            0 => "(objectClass=*)",
            1 => parts[0],
            _ => "(&" + string.Concat(parts) + ")",
        };
    }

    /// <summary>A filter in brackets: administrators write <c>objectClass=person</c> as often as <c>(objectClass=person)</c>.</summary>
    public static string Normalize(string filter)
    {
        string trimmed = filter.Trim();
        return trimmed.StartsWith('(') ? trimmed : "(" + trimmed + ")";
    }

    /// <summary>Whether a filter is at least well formed in its brackets (the server has the last word on the rest).</summary>
    public static bool IsBalanced(string filter)
    {
        int depth = 0;
        bool escaped = false;
        foreach (char c in filter)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    escaped = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return depth == 0 && filter.Contains('(');
    }

    /// <summary>Distinguished names compared the way directories do: case does not matter, nor the blanks around the separators.</summary>
    public static bool SameDn(string? first, string? second)
        => first is not null && second is not null
           && string.Equals(NormalizeDn(first), NormalizeDn(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDn(string dn)
        => string.Join(',', dn.Split(',').Select(part =>
        {
            int equals = part.IndexOf('=');
            return equals < 0 ? part.Trim() : part[..equals].Trim() + "=" + part[(equals + 1)..].Trim();
        }));
}

namespace MatMail.MailServer.Smtp;

/// <summary>The path of MAIL FROM / RCPT TO ("&lt;a@b&gt;", empty for "&lt;&gt;") and its ESMTP parameters (keys upper case).</summary>
internal sealed record SmtpPath(string Address, IReadOnlyDictionary<string, string?> Parameters);

/// <summary>Parsing of SMTP command lines (RFC 5321 section 4.1).</summary>
internal static class SmtpCommandParser
{
    /// <summary>Splits a command line into the verb (upper case) and its argument.</summary>
    public static (string Verb, string Argument) SplitCommand(string line)
    {
        string trimmed = line.Trim();
        int space = trimmed.IndexOf(' ');
        return space < 0
            ? (trimmed.ToUpperInvariant(), string.Empty)
            : (trimmed[..space].ToUpperInvariant(), trimmed[(space + 1)..].Trim());
    }

    /// <summary>
    /// Parses the argument of MAIL ("FROM:&lt;a@b&gt; SIZE=100") or RCPT ("TO:&lt;a@b&gt;"); <paramref name="prefix"/> is "FROM:" or "TO:".
    /// Lenient where clients commonly are (a space after the colon, a missing pair of angle brackets). Null when it cannot be read.
    /// </summary>
    public static SmtpPath? ParsePath(string argument, string prefix)
    {
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = argument[prefix.Length..].TrimStart();
        string address;
        string parameters;
        if (rest.StartsWith('<'))
        {
            int close = FindClosingBracket(rest);
            if (close < 0)
            {
                return null;
            }

            address = rest[1..close];
            parameters = rest[(close + 1)..];
            if (parameters.Length > 0 && parameters[0] != ' ')
            {
                return null;
            }
        }
        else
        {
            int space = rest.IndexOf(' ');
            address = space < 0 ? rest : rest[..space];
            parameters = space < 0 ? string.Empty : rest[space..];
            if (address.Length == 0)
            {
                return null;
            }
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string token in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = token.IndexOf('=');
            string key = (equals < 0 ? token : token[..equals]).ToUpperInvariant();
            if (key.Length == 0 || values.ContainsKey(key))
            {
                return null;
            }

            values[key] = equals < 0 ? null : token[(equals + 1)..];
        }

        return new SmtpPath(StripSourceRoute(address.Trim()), values);
    }

    /// <summary>Finds the '&gt;' that closes the path, skipping a quoted local part ("a&gt;b"@example.com).</summary>
    private static int FindClosingBracket(string text)
    {
        bool quoted = false;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted && c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == '>' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>"@relay1,@relay2:user@domain" becomes "user@domain": source routes are obsolete and ignored (RFC 5321 appendix C).</summary>
    private static string StripSourceRoute(string path)
    {
        if (!path.StartsWith('@'))
        {
            return path;
        }

        int colon = path.IndexOf(':');
        return colon < 0 ? path : path[(colon + 1)..];
    }
}

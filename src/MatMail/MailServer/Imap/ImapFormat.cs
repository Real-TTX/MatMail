using System.Globalization;
using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>How values are written in IMAP responses: strings, NIL, mailbox names, dates.</summary>
internal static class ImapFormat
{
    private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    /// <summary>A quoted string when possible (7-bit, no CR/LF), otherwise a literal of the UTF-8 bytes.</summary>
    public static string String(string value) => CanQuote(value) ? Quote(value) : Literal(value);

    /// <summary>NIL for null, otherwise <see cref="String"/>.</summary>
    public static string NString(string? value) => value is null ? "NIL" : String(value);

    public static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            if (character is '"' or '\\')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.Append('"').ToString();
    }

    public static string Literal(string value) => "{" + Encoding.UTF8.GetByteCount(value) + "}\r\n" + value;

    /// <summary>quoted = DQUOTE *QUOTED-CHAR DQUOTE, where QUOTED-CHAR is 7-bit text without CR and LF.</summary>
    public static bool CanQuote(string value)
    {
        foreach (char character in value)
        {
            if (character is '\0' or '\r' or '\n' || character > 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A mailbox name in a response: modified UTF-7, always quoted.</summary>
    public static string Mailbox(string name) => Quote(ModifiedUtf7.Encode(name));

    /// <summary>INTERNALDATE: "07-Oct-2026 10:00:00 +0000" (UTC).</summary>
    public static string InternalDate(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return "\"" + utc.ToString("dd", CultureInfo.InvariantCulture) + "-" + Months[utc.Month - 1] + "-" +
               utc.ToString("yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000\"";
    }

    /// <summary>date-time of APPEND: "dd-MMM-yyyy HH:mm:ss +hhmm" (the day may be one digit or space-padded).</summary>
    public static bool TryParseDateTime(string text, out DateTime utc)
    {
        utc = default;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !TryParseDate(parts[0], out DateTime date))
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(parts[1], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out TimeSpan time) || time.TotalHours >= 24)
        {
            return false;
        }

        string zone = parts[2];
        if (zone.Length != 5 || zone[0] is not ('+' or '-') || !int.TryParse(zone.AsSpan(1, 2), out int hours) || !int.TryParse(zone.AsSpan(3, 2), out int minutes)
            || hours > 14 || minutes > 59)
        {
            return false;
        }

        var offset = new TimeSpan(hours, minutes, 0);
        var local = new DateTimeOffset(date + time, zone[0] == '-' ? -offset : offset);
        utc = local.UtcDateTime;
        return true;
    }

    /// <summary>date of SEARCH: "d-MMM-yyyy" (one or two digit day).</summary>
    public static bool TryParseDate(string text, out DateTime date)
        => DateTime.TryParseExact(text.Trim(), new[] { "d-MMM-yyyy", "dd-MMM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

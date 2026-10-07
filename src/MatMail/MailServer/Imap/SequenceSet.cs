using System.Diagnostics.CodeAnalysis;

namespace MatMail.MailServer.Imap;

/// <summary>
/// A sequence set like "1:4,7,9:*" (RFC 3501, section 9). "*" stands for the largest number in use: the number of messages for
/// sequence numbers, the highest UID for UID sets. A range is valid in either direction ("5:2" equals "2:5").
/// </summary>
internal sealed class SequenceSet
{
    private const long Star = -1;

    private readonly List<(long First, long Last)> _ranges;

    private SequenceSet(List<(long First, long Last)> ranges) => _ranges = ranges;

    public static bool TryParse(string text, [NotNullWhen(true)] out SequenceSet? set)
    {
        set = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var ranges = new List<(long First, long Last)>();
        foreach (string part in text.Split(','))
        {
            int colon = part.IndexOf(':');
            string first = colon < 0 ? part : part[..colon];
            string last = colon < 0 ? part : part[(colon + 1)..];
            if (!TryParseNumber(first, out long start) || !TryParseNumber(last, out long end))
            {
                return false;
            }

            ranges.Add((start, end));
        }

        set = new SequenceSet(ranges);
        return true;
    }

    /// <summary>The ranges with "*" replaced by <paramref name="star"/>, each ordered from low to high.</summary>
    public IEnumerable<(long Low, long High)> Resolve(long star)
    {
        foreach ((long first, long last) in _ranges)
        {
            long a = first == Star ? star : first;
            long b = last == Star ? star : last;
            yield return a <= b ? (a, b) : (b, a);
        }
    }

    public bool Contains(long value, long star) => Resolve(star).Any(r => value >= r.Low && value <= r.High);

    public override string ToString()
        => string.Join(',', _ranges.Select(r => r.First == r.Last ? Format(r.First) : Format(r.First) + ":" + Format(r.Last)));

    /// <summary>Formats ascending numbers compactly ("1:3,7,9:10"), e.g. for COPYUID.</summary>
    public static string Format(IEnumerable<long> numbers)
    {
        var parts = new List<string>();
        long? start = null;
        long previous = 0;
        foreach (long number in numbers)
        {
            if (start is not null && number == previous + 1)
            {
                previous = number;
                continue;
            }

            if (start is not null)
            {
                parts.Add(start == previous ? $"{start}" : $"{start}:{previous}");
            }

            start = number;
            previous = number;
        }

        if (start is not null)
        {
            parts.Add(start == previous ? $"{start}" : $"{start}:{previous}");
        }

        return string.Join(',', parts);
    }

    private static string Format(long value) => value == Star ? "*" : value.ToString();

    private static bool TryParseNumber(string text, out long value)
    {
        if (text == "*")
        {
            value = Star;
            return true;
        }

        value = 0;
        return text.Length is > 0 and <= 18 && text.All(char.IsAsciiDigit) && long.TryParse(text, out value);
    }
}

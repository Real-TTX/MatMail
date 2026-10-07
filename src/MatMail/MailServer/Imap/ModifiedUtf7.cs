using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>
/// Modified UTF-7 for IMAP mailbox names (RFC 3501, section 5.1.3): printable ASCII stands for itself ("&amp;" becomes "&amp;-"),
/// everything else is written as modified BASE64 of UTF-16 between "&amp;" and "-", e.g. "Entwürfe" ⇄ "Entw&amp;APw-rfe".
/// </summary>
public static class ModifiedUtf7
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+,";

    public static string Encode(string text)
    {
        var result = new StringBuilder(text.Length + 8);
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            if (current == '&')
            {
                result.Append("&-");
                index++;
                continue;
            }

            if (IsPrintableAscii(current))
            {
                result.Append(current);
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && !IsPrintableAscii(text[index]))
            {
                index++;
            }

            result.Append('&');
            AppendBase64(result, text.AsSpan(start, index - start));
            result.Append('-');
        }

        return result.ToString();
    }

    /// <summary>Decodes a mailbox name. Sequences that are not valid modified UTF-7 are kept as they are (lenient).</summary>
    public static string Decode(string text)
    {
        if (!text.Contains('&'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            int end = current == '&' ? text.IndexOf('-', index + 1) : -1;
            if (end < 0)
            {
                result.Append(current);
                index++;
                continue;
            }

            if (end == index + 1)
            {
                result.Append('&');
            }
            else
            {
                string? decoded = DecodeBase64(text.AsSpan(index + 1, end - index - 1));
                result.Append(decoded ?? text.Substring(index, end - index + 1));
            }

            index = end + 1;
        }

        return result.ToString();
    }

    private static bool IsPrintableAscii(char value) => value >= 0x20 && value <= 0x7e;

    private static void AppendBase64(StringBuilder result, ReadOnlySpan<char> characters)
    {
        int buffer = 0;
        int bits = 0;
        foreach (char character in characters)
        {
            foreach (byte value in new[] { (byte)(character >> 8), (byte)character })
            {
                buffer = (buffer << 8) | value;
                bits += 8;
                while (bits >= 6)
                {
                    bits -= 6;
                    result.Append(Alphabet[(buffer >> bits) & 0x3f]);
                }

                buffer &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (6 - bits)) & 0x3f]);
        }
    }

    private static string? DecodeBase64(ReadOnlySpan<char> encoded)
    {
        var bytes = new List<byte>(encoded.Length);
        int buffer = 0;
        int bits = 0;
        foreach (char character in encoded)
        {
            int value = Alphabet.IndexOf(character);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 6) | value;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        if (bytes.Count == 0 || bytes.Count % 2 != 0)
        {
            return null;
        }

        var characters = new char[bytes.Count / 2];
        for (int i = 0; i < characters.Length; i++)
        {
            characters[i] = (char)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        }

        return new string(characters);
    }
}

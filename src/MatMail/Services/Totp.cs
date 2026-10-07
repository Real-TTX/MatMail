using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MatMail.Services;

/// <summary>Base32 (RFC 4648), the text form authenticator apps use for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Upper-case text without "=" padding (what the key URI format asks for).</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 / 5) + 1);
        int buffer = 0;
        int bits = 0;
        foreach (byte value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    /// <summary>Decodes Base32 text. Case, spaces, hyphens and "=" padding are ignored; any other character makes it fail.</summary>
    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var result = new List<byte>(text.Length * 5 / 8);
        int buffer = 0;
        int bits = 0;
        foreach (char c in text)
        {
            if (c is ' ' or '-' or '=')
            {
                continue;
            }

            int index = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        bytes = result.ToArray();
        return bytes.Length > 0;
    }
}

/// <summary>
/// Time-based one-time passwords (RFC 6238, built on the HOTP of RFC 4226): HMAC-SHA1, 30 second steps, 6 digits - what
/// Google Authenticator, Microsoft Authenticator, Aegis, 1Password and the like generate.
/// </summary>
public static class Totp
{
    public const int PeriodSeconds = 30;
    public const int CodeLength = 6;

    /// <summary>160 bits, the length RFC 4226 recommends (and the size of the HMAC-SHA1 block).</summary>
    public const int SecretLength = 20;

    /// <summary>How many steps before and after the current one still count: a phone clock that is a little off must not lock people out.</summary>
    public const int DefaultWindow = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    /// <summary>The number of the 30 second step a moment falls into (RFC 6238 "T").</summary>
    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>The code of one step, zero-padded to <paramref name="digits"/> digits (1 to 9).</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = CodeLength)
    {
        if (digits is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        // Dynamic truncation (RFC 4226 section 5.3).
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        int modulus = 1;
        for (int i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Does the code belong to one of the steps around <paramref name="currentStep"/>? Only steps after <paramref name="lastUsedStep"/>
    /// count, so a code that was accepted once is never accepted again (RFC 6238 section 5.2). When several steps produce the same
    /// code the latest one is reported, which is the one that gets remembered. The comparison takes the same time whatever matches.
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<byte> secret, string? code, long currentStep, long lastUsedStep, out long matchedStep, int window = DefaultWindow)
    {
        matchedStep = 0;
        string? digits = NormalizeCode(code);
        if (digits is null)
        {
            return false;
        }

        byte[] candidate = Encoding.ASCII.GetBytes(digits);
        bool found = false;
        for (long step = currentStep - window; step <= currentStep + window; step++)
        {
            byte[] expected = Encoding.ASCII.GetBytes(Compute(secret, step));
            if (CryptographicOperations.FixedTimeEquals(candidate, expected) && step > lastUsedStep)
            {
                matchedStep = step;
                found = true;
            }
        }

        return found;
    }

    /// <summary>The six digits of what a person typed ("123 456" is fine), or null when it is not a code.</summary>
    public static string? NormalizeCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new StringBuilder(CodeLength);
        foreach (char c in text)
        {
            if (c is ' ' or '\t')
            {
                continue;
            }

            if (c is < '0' or > '9')
            {
                return null;
            }

            digits.Append(c);
        }

        return digits.Length == CodeLength ? digits.ToString() : null;
    }

    /// <summary>
    /// The address a QR code carries (Key URI Format of Google Authenticator): "otpauth://totp/Issuer:account?secret=…".
    /// Authenticator apps show the issuer and the account name next to the code.
    /// </summary>
    public static string BuildUri(string issuer, string account, string secretBase32)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
         + $"?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={CodeLength}&period={PeriodSeconds}";
}

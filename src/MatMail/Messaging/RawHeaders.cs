using MimeKit;

namespace MatMail.Messaging;

/// <summary>Reads header fields of a raw message without parsing the body (which can be large).</summary>
public static class RawHeaders
{
    /// <summary>The index just after the empty line that ends the header block (the length of the message when there is none).</summary>
    public static int HeaderEnd(byte[] raw)
    {
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '\n' && raw[i + 1] == '\n')
            {
                return i + 2;
            }

            if (raw[i] == '\r' && i + 3 < raw.Length && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return raw.Length;
    }

    /// <summary>The header fields of the message; empty when they cannot be read.</summary>
    public static HeaderList Read(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, 0, HeaderEnd(raw), writable: false);
            return HeaderList.Load(ParserOptions.Default, stream);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new HeaderList();
        }
    }

    /// <summary>The value of the first header field of this name, or null.</summary>
    public static string? Get(byte[] raw, string name) => Read(raw)[name];

    /// <summary>The Message-ID of the message, or null.</summary>
    public static string? MessageId(byte[] raw) => Get(raw, "Message-ID")?.Trim();
}

using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>
/// One complete command as the client sent it: the text pieces with the literals between them. Text piece <c>i</c> ends with the
/// literal marker (<c>{n}</c> or <c>{n+}</c>) of literal <c>i</c>; the last text piece ends the command.
/// </summary>
internal sealed class ImapRequest
{
    public ImapRequest(IReadOnlyList<string> texts, IReadOnlyList<byte[]> literals, ImapRejection? rejection = null)
    {
        Texts = texts;
        Literals = literals;
        Rejection = rejection;
    }

    public IReadOnlyList<string> Texts { get; }

    public IReadOnlyList<byte[]> Literals { get; }

    /// <summary>Set when the command was refused while it was being read (a literal over the limit).</summary>
    public ImapRejection? Rejection { get; }

    /// <summary>The tag as far as it can be told from the first line ("*" when there is none).</summary>
    public string TagOrStar
    {
        get
        {
            string first = Texts[0];
            int space = first.IndexOf(' ');
            string tag = space < 0 ? first : first[..space];
            return ImapParser.IsValidTag(tag) ? tag : "*";
        }
    }
}

/// <summary>A command refused before it was parsed: the status ("NO"/"BAD") and the response text.</summary>
internal sealed record ImapRejection(string Status, string Text);

/// <summary>Reads complete commands from a connection: lines, synchronizing literals ("+ Ready") and LITERAL+.</summary>
internal sealed class ImapRequestReader
{
    /// <summary>Longest command line (without literals). Long UID lists of some clients need a generous limit.</summary>
    public const int MaxLineLength = 1024 * 1024;

    /// <summary>Largest message APPEND accepts (also advertised as APPENDLIMIT).</summary>
    public const int MaxAppendSize = 100 * 1024 * 1024;

    /// <summary>Largest literal of any other command (passwords, search strings, mailbox names).</summary>
    public const int MaxOtherLiteralSize = 1024 * 1024;

    /// <summary>A refused non-synchronizing literal larger than this is not even read: the connection is closed.</summary>
    private const long MaxDiscardSize = 200L * 1024 * 1024;

    private readonly ImapConnection _connection;

    public ImapRequestReader(ImapConnection connection) => _connection = connection;

    /// <summary>Reads the next command. Returns null when the client closed the connection.</summary>
    public async Task<ImapRequest?> ReadAsync(CancellationToken cancel)
    {
        byte[]? line = await _connection.ReadLineAsync(MaxLineLength, cancel);
        if (line is null)
        {
            return null;
        }

        var texts = new List<string> { Decode(line) };
        var literals = new List<byte[]>();
        bool isAppend = IsAppend(texts[0]);
        long literalLimit = isAppend ? MaxAppendSize : MaxOtherLiteralSize;
        long total = line.Length;

        while (TryGetLiteralMarker(texts[^1], out long size, out bool synchronizing))
        {
            if (size > literalLimit || total + size > MaxAppendSize + MaxOtherLiteralSize)
            {
                return await RefuseAsync(texts, size, synchronizing, isAppend, cancel);
            }

            if (synchronizing)
            {
                _connection.Write("+ Ready for literal data\r\n");
                await _connection.FlushAsync(cancel);
            }

            literals.Add(await _connection.ReadBytesAsync((int)size, cancel));
            total += size;

            line = await _connection.ReadLineAsync(MaxLineLength, cancel)
                ?? throw new EndOfStreamException("The client closed the connection in the middle of a command.");
            texts.Add(Decode(line));
            total += line.Length;
        }

        return new ImapRequest(texts, literals);
    }

    /// <summary>Recognises a literal marker ("{123}" or "{123+}") at the end of a line.</summary>
    public static bool TryGetLiteralMarker(string text, out long size, out bool synchronizing)
    {
        size = 0;
        synchronizing = true;
        if (text.Length < 3 || text[^1] != '}')
        {
            return false;
        }

        int open = text.LastIndexOf('{');
        if (open < 0)
        {
            return false;
        }

        ReadOnlySpan<char> digits = text.AsSpan(open + 1, text.Length - open - 2);
        if (digits.Length > 0 && digits[^1] == '+')
        {
            synchronizing = false;
            digits = digits[..^1];
        }

        if (digits.Length == 0 || digits.Length > 12)
        {
            return false;
        }

        foreach (char digit in digits)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }
        }

        size = long.Parse(digits);
        return true;
    }

    private async Task<ImapRequest> RefuseAsync(List<string> texts, long size, bool synchronizing, bool isAppend, CancellationToken cancel)
    {
        // A synchronizing literal is simply not asked for: the tagged answer ends the command. A non-synchronizing one is on its way
        // and has to be read and dropped together with the rest of the command.
        while (!synchronizing)
        {
            if (size > MaxDiscardSize)
            {
                throw new ImapLineTooLongException();
            }

            await _connection.SkipBytesAsync(size, cancel);
            byte[] line = await _connection.ReadLineAsync(MaxLineLength, cancel)
                ?? throw new EndOfStreamException("The client closed the connection in the middle of a command.");
            if (!TryGetLiteralMarker(Decode(line), out size, out synchronizing))
            {
                break;
            }
        }

        ImapRejection rejection = isAppend
            ? new ImapRejection("NO", $"[TOOBIG] The message is larger than the allowed {MaxAppendSize / (1024 * 1024)} MB.")
            : new ImapRejection("BAD", "The literal is too large.");
        return new ImapRequest(texts, Array.Empty<byte[]>(), rejection);
    }

    private static bool IsAppend(string firstLine)
    {
        int space = firstLine.IndexOf(' ');
        if (space < 0)
        {
            return false;
        }

        ReadOnlySpan<char> rest = firstLine.AsSpan(space + 1);
        return rest.StartsWith("APPEND ", StringComparison.OrdinalIgnoreCase);
    }

    private static string Decode(byte[] line) => Encoding.UTF8.GetString(line);
}

using System.Text;

namespace MatMail.MailServer.Imap;

/// <summary>A command the server cannot understand: answered with a tagged BAD.</summary>
internal sealed class ImapSyntaxException : Exception
{
    public ImapSyntaxException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads the tokens of one command (RFC 3501, section 9): atoms, quoted strings, literals, lists, numbers. Literals are taken from
/// the request where their marker stands. The parser is lenient with extra spaces and with 8-bit characters in atoms.
/// </summary>
internal sealed class ImapParser
{
    private readonly ImapRequest _request;
    private int _segment;
    private int _position;

    public ImapParser(ImapRequest request) => _request = request;

    public bool AtEnd => _position >= Text.Length && _segment == _request.Texts.Count - 1;

    private string Text => _request.Texts[_segment];

    /// <summary>tag = 1*&lt;any ASTRING-CHAR except "+"&gt;.</summary>
    public static bool IsValidTag(string tag) => tag.Length > 0 && tag.All(c => IsAStringChar(c) && c != '+');

    /// <summary>ATOM-CHAR: any CHAR except atom-specials; 8-bit characters are tolerated.</summary>
    public static bool IsAtomChar(char value)
        => value > 0x20 && value != 0x7f && value is not ('(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']');

    public static bool IsAStringChar(char value) => IsAtomChar(value) || value == ']';

    public static bool IsListChar(char value) => IsAStringChar(value) || value is '%' or '*';

    public char Peek() => _position < Text.Length ? Text[_position] : '\0';

    public char PeekAt(int offset) => _position + offset < Text.Length ? Text[_position + offset] : '\0';

    public bool TryConsume(char value)
    {
        if (Peek() != value)
        {
            return false;
        }

        _position++;
        return true;
    }

    public void Expect(char value)
    {
        if (!TryConsume(value))
        {
            throw Error($"Expected '{value}'.");
        }
    }

    public bool TrySpace()
    {
        if (Peek() != ' ')
        {
            return false;
        }

        while (Peek() == ' ')
        {
            _position++;
        }

        return true;
    }

    public void ExpectSpace()
    {
        if (!TrySpace())
        {
            throw Error("Expected a space.");
        }
    }

    /// <summary>The command must be complete here (trailing spaces are tolerated).</summary>
    public void ExpectEnd()
    {
        TrySpace();
        if (!AtEnd)
        {
            throw Error("Unexpected text at the end of the command.");
        }
    }

    public string ReadTag()
    {
        string tag = ReadWhile(c => IsAStringChar(c) && c != '+');
        if (tag.Length == 0)
        {
            throw Error("Missing tag.");
        }

        return tag;
    }

    public string ReadAtom()
    {
        string atom = ReadWhile(IsAtomChar);
        if (atom.Length == 0)
        {
            throw Error("Expected an atom.");
        }

        return atom;
    }

    /// <summary>Reads the next atom when it equals <paramref name="word"/> (case-insensitive); otherwise nothing is consumed.</summary>
    public bool TryReadWord(string word)
    {
        if (string.Compare(Text, _position, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        int end = _position + word.Length;
        if (end < Text.Length && IsAtomChar(Text[end]))
        {
            return false;
        }

        _position = end;
        return true;
    }

    /// <summary>astring = 1*ASTRING-CHAR / string.</summary>
    public string ReadAString()
    {
        char next = Peek();
        if (next is '"' or '{' or '~')
        {
            return ReadString();
        }

        string atom = ReadWhile(IsAStringChar);
        if (atom.Length == 0)
        {
            throw Error("Expected a string.");
        }

        return atom;
    }

    /// <summary>string = quoted / literal.</summary>
    public string ReadString()
    {
        return Peek() switch
        {
            '"' => ReadQuoted(),
            '{' => Encoding.UTF8.GetString(ReadLiteral()),
            '~' => throw Error("Binary literals are not supported."),
            _ => throw Error("Expected a quoted string or a literal."),
        };
    }

    /// <summary>nstring = string / NIL.</summary>
    public string? ReadNString() => TryReadWord("NIL") ? null : ReadString();

    /// <summary>The raw bytes of a literal (or of a quoted string), e.g. the message of APPEND.</summary>
    public byte[] ReadBytes()
    {
        return Peek() switch
        {
            '{' => ReadLiteral(),
            '"' => Encoding.UTF8.GetBytes(ReadQuoted()),
            '~' => throw Error("Binary literals are not supported."),
            _ => throw Error("Expected a literal."),
        };
    }

    /// <summary>A mailbox name: astring, decoded from modified UTF-7.</summary>
    public string ReadMailbox() => ModifiedUtf7.Decode(ReadAString());

    /// <summary>list-mailbox = 1*list-char / string (wildcards allowed), decoded from modified UTF-7.</summary>
    public string ReadListMailbox()
    {
        if (Peek() is '"' or '{')
        {
            return ModifiedUtf7.Decode(ReadString());
        }

        string pattern = ReadWhile(IsListChar);
        if (pattern.Length == 0)
        {
            throw Error("Expected a mailbox pattern.");
        }

        return ModifiedUtf7.Decode(pattern);
    }

    public long ReadNumber()
    {
        string digits = ReadWhile(char.IsAsciiDigit);
        if (digits.Length == 0 || digits.Length > 18)
        {
            throw Error("Expected a number.");
        }

        return long.Parse(digits);
    }

    /// <summary>The text of a sequence set ("1:4,7,9:*"); validated by <see cref="SequenceSet"/>.</summary>
    public SequenceSet ReadSequenceSet()
    {
        string text = ReadWhile(c => char.IsAsciiDigit(c) || c is ':' or ',' or '*');
        if (!SequenceSet.TryParse(text, out SequenceSet? set))
        {
            throw Error("Invalid sequence set.");
        }

        return set;
    }

    /// <summary>A system flag ("\Seen"), a keyword ("$Label1") or "\*".</summary>
    public string ReadFlag()
    {
        if (TryConsume('\\'))
        {
            if (TryConsume('*'))
            {
                return "\\*";
            }

            return "\\" + ReadAtom();
        }

        return ReadAtom();
    }

    /// <summary>flag-list = "(" [flag *(SP flag)] ")".</summary>
    public List<string> ReadFlagList()
    {
        Expect('(');
        var flags = new List<string>();
        TrySpace();
        while (!TryConsume(')'))
        {
            flags.Add(ReadFlag());
            if (Peek() != ')')
            {
                ExpectSpace();
            }
        }

        return flags;
    }

    /// <summary>Flags in parentheses, or one or more flags separated by spaces up to the end of the command (STORE allows both).</summary>
    public List<string> ReadFlagsLoose()
    {
        if (Peek() == '(')
        {
            return ReadFlagList();
        }

        var flags = new List<string> { ReadFlag() };
        while (TrySpace() && !AtEnd)
        {
            flags.Add(ReadFlag());
        }

        return flags;
    }

    /// <summary>"(" atom *(SP atom) ")" — e.g. STATUS items or LIST options; an empty list is allowed.</summary>
    public List<string> ReadAtomList()
    {
        Expect('(');
        var items = new List<string>();
        TrySpace();
        while (!TryConsume(')'))
        {
            items.Add(ReadAtom());
            if (Peek() != ')')
            {
                ExpectSpace();
            }
        }

        return items;
    }

    /// <summary>Reads characters while they match (within the current text piece).</summary>
    public string ReadWhile(Func<char, bool> predicate)
    {
        int start = _position;
        while (_position < Text.Length && predicate(Text[_position]))
        {
            _position++;
        }

        return Text[start.._position];
    }

    public ImapSyntaxException Error(string message) => new(message);

    private string ReadQuoted()
    {
        Expect('"');
        var value = new StringBuilder();
        while (true)
        {
            if (_position >= Text.Length)
            {
                throw Error("Unterminated quoted string.");
            }

            char current = Text[_position++];
            if (current == '"')
            {
                return value.ToString();
            }

            if (current == '\\')
            {
                if (_position >= Text.Length)
                {
                    throw Error("Unterminated quoted string.");
                }

                current = Text[_position++];
            }

            value.Append(current);
        }
    }

    private byte[] ReadLiteral()
    {
        // The marker must end this text piece; the reader has already collected the literal that follows it.
        int close = Text.IndexOf('}', _position);
        if (close != Text.Length - 1 || _segment >= _request.Literals.Count)
        {
            throw Error("Invalid literal.");
        }

        byte[] value = _request.Literals[_segment];
        _segment++;
        _position = 0;
        return value;
    }
}

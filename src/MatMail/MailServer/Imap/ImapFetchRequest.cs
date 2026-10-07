namespace MatMail.MailServer.Imap;

internal enum ImapFetchItemKind
{
    Uid,
    Flags,
    InternalDate,
    Size,
    Envelope,
    BodyStructure,

    /// <summary>BODY without a section: the non-extensible body structure.</summary>
    Body,

    /// <summary>BODY[...] / BODY.PEEK[...].</summary>
    Section,
    Rfc822,
    Rfc822Header,
    Rfc822Text,
}

/// <summary>One data item of a FETCH command.</summary>
internal sealed record ImapFetchItem(ImapFetchItemKind Kind, ImapSection? Section = null, bool Peek = false, long? Origin = null, long? Count = null)
{
    /// <summary>The name the response uses for this item ("BODY[HEADER]&lt;0&gt;", "RFC822.SIZE", ...).</summary>
    public string ResponseName => Kind switch
    {
        ImapFetchItemKind.Uid => "UID",
        ImapFetchItemKind.Flags => "FLAGS",
        ImapFetchItemKind.InternalDate => "INTERNALDATE",
        ImapFetchItemKind.Size => "RFC822.SIZE",
        ImapFetchItemKind.Envelope => "ENVELOPE",
        ImapFetchItemKind.BodyStructure => "BODYSTRUCTURE",
        ImapFetchItemKind.Body => "BODY",
        ImapFetchItemKind.Rfc822 => "RFC822",
        ImapFetchItemKind.Rfc822Header => "RFC822.HEADER",
        ImapFetchItemKind.Rfc822Text => "RFC822.TEXT",
        _ => "BODY[" + Section + "]" + (Origin is null ? string.Empty : "<" + Origin + ">"),
    };

    /// <summary>Reading this item marks the message \Seen (BODY[...] without PEEK, RFC822, RFC822.TEXT).</summary>
    public bool SetsSeen => (Kind == ImapFetchItemKind.Section && !Peek) || Kind is ImapFetchItemKind.Rfc822 or ImapFetchItemKind.Rfc822Text;

    /// <summary>Needs the complete raw message (header-only items are served from the stored header block).</summary>
    public bool NeedsRaw => Kind is ImapFetchItemKind.Rfc822 or ImapFetchItemKind.Rfc822Text
                            || (Kind == ImapFetchItemKind.Section && !Section!.IsTopLevelHeader);

    public bool NeedsHeader => Kind == ImapFetchItemKind.Rfc822Header || (Kind == ImapFetchItemKind.Section && Section!.IsTopLevelHeader);
}

/// <summary>The parsed data items of a FETCH command (RFC 3501, section 6.4.5).</summary>
internal sealed class ImapFetchRequest
{
    private ImapFetchRequest(List<ImapFetchItem> items) => Items = items;

    public List<ImapFetchItem> Items { get; }

    public bool Has(ImapFetchItemKind kind) => Items.Any(i => i.Kind == kind);

    public bool NeedsRaw => Items.Any(i => i.NeedsRaw);

    public bool NeedsHeader => Items.Any(i => i.NeedsHeader);

    public bool NeedsMetadata => Has(ImapFetchItemKind.InternalDate) || Has(ImapFetchItemKind.Size);

    public bool SetsSeen => Items.Any(i => i.SetsSeen);

    /// <summary>Reads "ALL", "FAST", "FULL", one item or a parenthesised list of items.</summary>
    public static ImapFetchRequest Parse(ImapParser parser, bool isUid)
    {
        var items = new List<ImapFetchItem>();
        if (parser.TryConsume('('))
        {
            parser.TrySpace();
            while (!parser.TryConsume(')'))
            {
                ReadItem(parser, items, allowMacro: false);
                if (parser.Peek() != ')')
                {
                    parser.ExpectSpace();
                }
            }
        }
        else
        {
            ReadItem(parser, items, allowMacro: true);
        }

        if (isUid && !items.Any(i => i.Kind == ImapFetchItemKind.Uid))
        {
            items.Insert(0, new ImapFetchItem(ImapFetchItemKind.Uid));
        }

        // The same item asked twice is answered once.
        return new ImapFetchRequest(items.Distinct().ToList());
    }

    private static void ReadItem(ImapParser parser, List<ImapFetchItem> items, bool allowMacro)
    {
        string name = parser.ReadWhile(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToUpperInvariant();
        switch (name)
        {
            case "ALL" or "FAST" or "FULL" when allowMacro:
                items.Add(new ImapFetchItem(ImapFetchItemKind.Flags));
                items.Add(new ImapFetchItem(ImapFetchItemKind.InternalDate));
                items.Add(new ImapFetchItem(ImapFetchItemKind.Size));
                if (name != "FAST")
                {
                    items.Add(new ImapFetchItem(ImapFetchItemKind.Envelope));
                }

                if (name == "FULL")
                {
                    items.Add(new ImapFetchItem(ImapFetchItemKind.Body));
                }

                return;
            case "UID":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Uid));
                return;
            case "FLAGS":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Flags));
                return;
            case "INTERNALDATE":
                items.Add(new ImapFetchItem(ImapFetchItemKind.InternalDate));
                return;
            case "RFC822.SIZE":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Size));
                return;
            case "ENVELOPE":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Envelope));
                return;
            case "BODYSTRUCTURE":
                items.Add(new ImapFetchItem(ImapFetchItemKind.BodyStructure));
                return;
            case "RFC822":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Rfc822));
                return;
            case "RFC822.HEADER":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Rfc822Header));
                return;
            case "RFC822.TEXT":
                items.Add(new ImapFetchItem(ImapFetchItemKind.Rfc822Text));
                return;
            case "BODY" when parser.Peek() != '[':
                items.Add(new ImapFetchItem(ImapFetchItemKind.Body));
                return;
            case "BODY" or "BODY.PEEK":
                items.Add(ReadSectionItem(parser, peek: name == "BODY.PEEK"));
                return;
            default:
                throw parser.Error(name.Length == 0 ? "Expected a fetch item." : $"Unknown or unsupported fetch item {name}.");
        }
    }

    private static ImapFetchItem ReadSectionItem(ImapParser parser, bool peek)
    {
        parser.Expect('[');
        ImapSection section = ReadSection(parser);
        parser.Expect(']');

        if (!parser.TryConsume('<'))
        {
            return new ImapFetchItem(ImapFetchItemKind.Section, section, peek);
        }

        long origin = parser.ReadNumber();
        parser.Expect('.');
        long count = parser.ReadNumber();
        parser.Expect('>');
        if (count == 0)
        {
            throw parser.Error("The length of a partial fetch must not be zero.");
        }

        return new ImapFetchItem(ImapFetchItemKind.Section, section, peek, origin, count);
    }

    /// <summary>section-spec = section-msgtext / (section-part ["." section-text]).</summary>
    private static ImapSection ReadSection(ImapParser parser)
    {
        var path = new List<int>();
        while (char.IsAsciiDigit(parser.Peek()))
        {
            long number = parser.ReadNumber();
            if (number is < 1 or > 100_000)
            {
                throw parser.Error("Invalid section part number.");
            }

            path.Add((int)number);
            if (parser.Peek() != '.' || !char.IsAsciiLetterOrDigit(parser.PeekAt(1)))
            {
                break;
            }

            parser.Expect('.');
        }

        string text = parser.ReadWhile(c => char.IsAsciiLetter(c) || c == '.').ToUpperInvariant();
        ImapSectionKind kind = text switch
        {
            "" => ImapSectionKind.Content,
            "HEADER" => ImapSectionKind.Header,
            "HEADER.FIELDS" => ImapSectionKind.HeaderFields,
            "HEADER.FIELDS.NOT" => ImapSectionKind.HeaderFieldsNot,
            "TEXT" => ImapSectionKind.Text,
            "MIME" when path.Count > 0 => ImapSectionKind.Mime,
            _ => throw parser.Error("Invalid body section."),
        };

        string[] fields = Array.Empty<string>();
        if (kind is ImapSectionKind.HeaderFields or ImapSectionKind.HeaderFieldsNot)
        {
            parser.ExpectSpace();
            fields = ReadHeaderList(parser);
        }

        return new ImapSection(path.ToArray(), kind, fields);
    }

    /// <summary>header-list = "(" header-fld-name *(SP header-fld-name) ")".</summary>
    private static string[] ReadHeaderList(ImapParser parser)
    {
        parser.Expect('(');
        var fields = new List<string>();
        parser.TrySpace();
        while (!parser.TryConsume(')'))
        {
            fields.Add(parser.ReadAString());
            if (parser.Peek() != ')')
            {
                parser.ExpectSpace();
            }
        }

        if (fields.Count == 0)
        {
            throw parser.Error("The header list must not be empty.");
        }

        return fields.ToArray();
    }
}

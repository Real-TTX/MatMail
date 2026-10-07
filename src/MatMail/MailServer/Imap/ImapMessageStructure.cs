using System.Text;
using MimeKit;

namespace MatMail.MailServer.Imap;

/// <summary>A MIME entity of a message with the byte offsets of its header and content in the raw message.</summary>
internal sealed class ImapBodyPart
{
    public required MimeEntity Entity { get; init; }

    /// <summary>Where the part's MIME header starts.</summary>
    public long Start { get; init; }

    /// <summary>Where the content starts (after the blank line).</summary>
    public long BodyStart { get; init; }

    /// <summary>Where the content ends (the line break before the next boundary belongs to the boundary).</summary>
    public long End { get; init; }

    /// <summary>The content length in lines (in its transfer encoding).</summary>
    public int Lines { get; init; }

    public List<ImapBodyPart> Children { get; } = new();

    /// <summary>The embedded message of a message/rfc822 part.</summary>
    public ImapMessagePart? Message { get; set; }

    public bool IsMultipart => Entity is Multipart;

    public long Size => Math.Max(0, End - BodyStart);
}

/// <summary>A message (the top-level one or an embedded message/rfc822) with its header and body offsets.</summary>
internal sealed class ImapMessagePart
{
    public required MimeMessage Message { get; init; }

    public long Start { get; init; }

    public long BodyStart { get; init; }

    public long End { get; init; }

    public required ImapBodyPart Body { get; init; }
}

/// <summary>What a body section names (RFC 3501, section 6.4.5).</summary>
internal enum ImapSectionKind
{
    /// <summary>"[]" (the whole message) or "[1.2]" (the content of a part).</summary>
    Content,
    Header,
    HeaderFields,
    HeaderFieldsNot,
    Text,
    Mime,
}

/// <summary>A body section like "1.2.HEADER.FIELDS (From To)".</summary>
internal sealed record ImapSection(int[] Path, ImapSectionKind Kind, string[] Fields)
{
    /// <summary>A header section of the message itself: served from the stored header block, without loading the message.</summary>
    public bool IsTopLevelHeader => Path.Length == 0 && Kind is ImapSectionKind.Header or ImapSectionKind.HeaderFields or ImapSectionKind.HeaderFieldsNot;

    /// <summary>The section as it is echoed in the FETCH response.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(string.Join('.', Path));
        string? kind = Kind switch
        {
            ImapSectionKind.Header => "HEADER",
            ImapSectionKind.HeaderFields => "HEADER.FIELDS",
            ImapSectionKind.HeaderFieldsNot => "HEADER.FIELDS.NOT",
            ImapSectionKind.Text => "TEXT",
            ImapSectionKind.Mime => "MIME",
            _ => null,
        };

        if (kind is not null)
        {
            text.Append(text.Length > 0 ? "." : string.Empty).Append(kind);
        }

        if (Kind is ImapSectionKind.HeaderFields or ImapSectionKind.HeaderFieldsNot)
        {
            text.Append(" (").Append(string.Join(' ', Fields.Select(f => f.ToUpperInvariant()))).Append(')');
        }

        return text.ToString();
    }
}

/// <summary>
/// A raw message parsed for IMAP: the MIME tree with exact byte offsets (from MimeKit's parser), so body sections are cut from the
/// original bytes and BODYSTRUCTURE sizes match them exactly.
/// </summary>
internal sealed class ImapMessageStructure
{
    private ImapMessageStructure(byte[] raw, ImapMessagePart root)
    {
        Raw = raw;
        Root = root;
    }

    public byte[] Raw { get; }

    public ImapMessagePart Root { get; }

    public static ImapMessageStructure Parse(byte[] raw)
    {
        var entities = new Dictionary<MimeEntity, (long Begin, long HeadersEnd, long End, int Lines)>(ReferenceEqualityComparer.Instance);
        var messages = new Dictionary<MimeMessage, (long Begin, long HeadersEnd, long End)>(ReferenceEqualityComparer.Instance);

        using var stream = new MemoryStream(raw, writable: false);
        var parser = new MimeParser(ParserOptions.Default, stream, MimeFormat.Entity, persistent: true);
        parser.MimeEntityEnd += (_, e) => entities[e.Entity] = (e.BeginOffset, e.HeadersEndOffset, e.EndOffset, e.Lines);
        parser.MimeMessageEnd += (_, e) => messages[e.Message] = (e.BeginOffset, e.HeadersEndOffset, e.EndOffset);
        MimeMessage message = parser.ParseMessage();

        return new ImapMessageStructure(raw, BuildMessage(message, entities, messages));
    }

    /// <summary>
    /// The part a section path names. Part 1 of a message that is not multipart is its body; the numbers below a message/rfc822
    /// part count the parts of the embedded message.
    /// </summary>
    public ImapBodyPart? FindPart(IReadOnlyList<int> path)
    {
        ImapBodyPart container = Root.Body;
        bool containerIsMessageBody = true;
        ImapBodyPart? current = null;

        foreach (int number in path)
        {
            if (container.IsMultipart)
            {
                current = number >= 1 && number <= container.Children.Count ? container.Children[number - 1] : null;
            }
            else
            {
                current = containerIsMessageBody && number == 1 ? container : null;
            }

            if (current is null)
            {
                return null;
            }

            containerIsMessageBody = current.Message is not null;
            container = current.Message?.Body ?? current;
        }

        return current;
    }

    /// <summary>The bytes of a body section; null when the section does not exist in this message.</summary>
    public ReadOnlyMemory<byte>? GetSection(ImapSection section)
    {
        if (section.Path.Length == 0)
        {
            return section.Kind == ImapSectionKind.Content ? Raw : GetMessageSection(Root, section);
        }

        ImapBodyPart? part = FindPart(section.Path);
        if (part is null)
        {
            return null;
        }

        return section.Kind switch
        {
            ImapSectionKind.Content => Slice(part.BodyStart, part.End),
            ImapSectionKind.Mime => Slice(part.Start, part.BodyStart),
            _ => part.Message is null ? null : GetMessageSection(part.Message, section),
        };
    }

    /// <summary>
    /// HEADER.FIELDS / HEADER.FIELDS.NOT: the matching header fields (with their continuation lines) followed by the blank line.
    /// </summary>
    public static byte[] FilterHeader(ReadOnlySpan<byte> header, IReadOnlyCollection<string> fields, bool include)
    {
        using var output = new MemoryStream(header.Length);
        int position = 0;
        ReadOnlySpan<byte> blankLine = ReadOnlySpan<byte>.Empty;

        while (position < header.Length)
        {
            int lineEnd = NextLineEnd(header, position);
            ReadOnlySpan<byte> line = header[position..lineEnd];
            if (IsBlankLine(line))
            {
                blankLine = line;
                break;
            }

            int fieldEnd = lineEnd;
            while (fieldEnd < header.Length && header[fieldEnd] is (byte)' ' or (byte)'\t')
            {
                fieldEnd = NextLineEnd(header, fieldEnd);
            }

            int colon = header[position..fieldEnd].IndexOf((byte)':');
            if (colon > 0)
            {
                string name = Encoding.ASCII.GetString(header.Slice(position, colon)).Trim();
                bool listed = fields.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
                if (listed == include)
                {
                    output.Write(header[position..fieldEnd]);
                }
            }

            position = fieldEnd;
        }

        output.Write(blankLine.IsEmpty ? "\r\n"u8 : blankLine);
        return output.ToArray();
    }

    private ReadOnlyMemory<byte>? GetMessageSection(ImapMessagePart message, ImapSection section)
    {
        ReadOnlyMemory<byte> header = Slice(message.Start, message.BodyStart);
        return section.Kind switch
        {
            ImapSectionKind.Header => header,
            ImapSectionKind.HeaderFields => FilterHeader(header.Span, section.Fields, include: true),
            ImapSectionKind.HeaderFieldsNot => FilterHeader(header.Span, section.Fields, include: false),
            ImapSectionKind.Text => Slice(message.BodyStart, message.End),
            _ => null,
        };
    }

    private ReadOnlyMemory<byte> Slice(long start, long end)
    {
        long from = Math.Clamp(start, 0, Raw.Length);
        long to = Math.Clamp(end, from, Raw.Length);
        return Raw.AsMemory((int)from, (int)(to - from));
    }

    private static int NextLineEnd(ReadOnlySpan<byte> bytes, int position)
    {
        int newline = bytes[position..].IndexOf((byte)'\n');
        return newline < 0 ? bytes.Length : position + newline + 1;
    }

    private static bool IsBlankLine(ReadOnlySpan<byte> line)
        => line.SequenceEqual("\r\n"u8) || line.SequenceEqual("\n"u8);

    private static ImapMessagePart BuildMessage(
        MimeMessage message,
        Dictionary<MimeEntity, (long Begin, long HeadersEnd, long End, int Lines)> entities,
        Dictionary<MimeMessage, (long Begin, long HeadersEnd, long End)> messages)
    {
        (long begin, long headersEnd, long end) = messages.GetValueOrDefault(message);
        ImapBodyPart body = message.Body is { } entity
            ? BuildPart(entity, entities, messages)
            : new ImapBodyPart { Entity = new TextPart("plain"), Start = begin, BodyStart = headersEnd, End = end };
        return new ImapMessagePart
        {
            Message = message,
            Start = begin,
            BodyStart = headersEnd,
            End = end,
            Body = body,
        };
    }

    private static ImapBodyPart BuildPart(
        MimeEntity entity,
        Dictionary<MimeEntity, (long Begin, long HeadersEnd, long End, int Lines)> entities,
        Dictionary<MimeMessage, (long Begin, long HeadersEnd, long End)> messages)
    {
        (long begin, long headersEnd, long end, int lines) = entities.GetValueOrDefault(entity);
        var part = new ImapBodyPart { Entity = entity, Start = begin, BodyStart = headersEnd, End = end, Lines = lines };

        if (entity is Multipart multipart)
        {
            foreach (MimeEntity child in multipart)
            {
                part.Children.Add(BuildPart(child, entities, messages));
            }
        }
        else if (entity is MessagePart { Message: { } embedded } && messages.ContainsKey(embedded))
        {
            part.Message = BuildMessage(embedded, entities, messages);
        }

        return part;
    }
}

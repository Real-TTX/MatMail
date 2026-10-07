using System.Globalization;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;

namespace MatMail.MailServer.Imap;

/// <summary>A SEARCH criterion (RFC 3501, section 6.4.4).</summary>
internal abstract record SearchKey;

internal sealed record AllKey : SearchKey;

/// <summary>Matches nothing (RECENT and NEW: this server never sets \Recent).</summary>
internal sealed record NothingKey : SearchKey;

internal sealed record FlagKey(ImapFlags Flag, bool IsSet) : SearchKey;

internal sealed record KeywordKey(string Keyword, bool IsSet) : SearchKey;

internal enum SearchDateComparison
{
    Before,
    On,
    Since,
}

internal sealed record DateKey(bool SentDate, SearchDateComparison Comparison, DateTime Date) : SearchKey;

internal sealed record SizeKey(bool Larger, long Size) : SearchKey;

/// <summary>FROM, TO, CC, BCC, SUBJECT and HEADER: the field contains the text (an empty text matches every message with the field).</summary>
internal sealed record HeaderKey(string Field, string Value) : SearchKey;

internal sealed record BodyKey(string Value) : SearchKey;

internal sealed record TextKey(string Value) : SearchKey;

internal sealed record UidKey(SequenceSet Set) : SearchKey;

internal sealed record SequenceKey(SequenceSet Set) : SearchKey;

internal sealed record NotKey(SearchKey Key) : SearchKey;

internal sealed record OrKey(SearchKey Left, SearchKey Right) : SearchKey;

internal sealed record AndKey(IReadOnlyList<SearchKey> Keys) : SearchKey;

/// <summary>Reads the criteria of a SEARCH command.</summary>
internal static class ImapSearchParser
{
    public static readonly string[] SupportedCharsets = { "UTF-8", "US-ASCII" };

    /// <summary>
    /// [CHARSET SP astring SP] search-key *(SP search-key). Returns the criteria and the charset named by the client (null when none
    /// was named); the caller refuses charsets it does not know.
    /// </summary>
    public static (SearchKey Key, string? Charset) Parse(ImapParser parser)
    {
        string? charset = null;
        if (parser.TryReadWord("CHARSET"))
        {
            parser.ExpectSpace();
            charset = parser.ReadAString();
            parser.ExpectSpace();
        }

        var keys = new List<SearchKey> { ReadKey(parser) };
        while (parser.TrySpace() && !parser.AtEnd)
        {
            keys.Add(ReadKey(parser));
        }

        return (keys.Count == 1 ? keys[0] : new AndKey(keys), charset);
    }

    private static SearchKey ReadKey(ImapParser parser)
    {
        if (parser.TryConsume('('))
        {
            var keys = new List<SearchKey>();
            parser.TrySpace();
            while (!parser.TryConsume(')'))
            {
                keys.Add(ReadKey(parser));
                if (parser.Peek() != ')')
                {
                    parser.ExpectSpace();
                }
            }

            if (keys.Count == 0)
            {
                throw parser.Error("Empty search group.");
            }

            return keys.Count == 1 ? keys[0] : new AndKey(keys);
        }

        if (char.IsAsciiDigit(parser.Peek()) || parser.Peek() == '*')
        {
            return new SequenceKey(parser.ReadSequenceSet());
        }

        string name = parser.ReadAtom().ToUpperInvariant();
        return name switch
        {
            "ALL" => new AllKey(),
            "ANSWERED" => new FlagKey(ImapFlags.Answered, true),
            "DELETED" => new FlagKey(ImapFlags.Deleted, true),
            "DRAFT" => new FlagKey(ImapFlags.Draft, true),
            "FLAGGED" => new FlagKey(ImapFlags.Flagged, true),
            "SEEN" => new FlagKey(ImapFlags.Seen, true),
            "UNANSWERED" => new FlagKey(ImapFlags.Answered, false),
            "UNDELETED" => new FlagKey(ImapFlags.Deleted, false),
            "UNDRAFT" => new FlagKey(ImapFlags.Draft, false),
            "UNFLAGGED" => new FlagKey(ImapFlags.Flagged, false),
            "UNSEEN" => new FlagKey(ImapFlags.Seen, false),
            "RECENT" or "NEW" => new NothingKey(),
            "OLD" => new AllKey(),
            "KEYWORD" => Keyword(parser, true),
            "UNKEYWORD" => Keyword(parser, false),
            "BEFORE" => Date(parser, false, SearchDateComparison.Before),
            "ON" => Date(parser, false, SearchDateComparison.On),
            "SINCE" => Date(parser, false, SearchDateComparison.Since),
            "SENTBEFORE" => Date(parser, true, SearchDateComparison.Before),
            "SENTON" => Date(parser, true, SearchDateComparison.On),
            "SENTSINCE" => Date(parser, true, SearchDateComparison.Since),
            "LARGER" => Size(parser, true),
            "SMALLER" => Size(parser, false),
            "FROM" or "TO" or "CC" or "BCC" or "SUBJECT" => Header(parser, name),
            "HEADER" => Header(parser, null),
            "BODY" => new BodyKey(Argument(parser)),
            "TEXT" => new TextKey(Argument(parser)),
            "UID" => Uid(parser),
            "NOT" => Not(parser),
            "OR" => Or(parser),
            _ => throw parser.Error($"Unknown search criterion {name}."),
        };
    }

    private static string Argument(ImapParser parser)
    {
        parser.ExpectSpace();
        return parser.ReadAString();
    }

    private static SearchKey Keyword(ImapParser parser, bool isSet)
    {
        parser.ExpectSpace();
        string flag = parser.ReadFlag();
        if (ImapFlagNames.TryParse(flag, out ImapFlags systemFlag))
        {
            return new FlagKey(systemFlag, isSet);
        }

        return new KeywordKey(flag, isSet);
    }

    private static SearchKey Date(ImapParser parser, bool sentDate, SearchDateComparison comparison)
    {
        parser.ExpectSpace();
        string text = parser.Peek() == '"' ? parser.ReadString() : parser.ReadAtom();
        if (!ImapFormat.TryParseDate(text, out DateTime date))
        {
            throw parser.Error("Invalid date.");
        }

        return new DateKey(sentDate, comparison, date.Date);
    }

    private static SearchKey Size(ImapParser parser, bool larger)
    {
        parser.ExpectSpace();
        return new SizeKey(larger, parser.ReadNumber());
    }

    private static SearchKey Header(ImapParser parser, string? field)
    {
        parser.ExpectSpace();
        if (field is null)
        {
            field = parser.ReadAString();
            parser.ExpectSpace();
        }

        return new HeaderKey(field, parser.ReadAString());
    }

    private static SearchKey Uid(ImapParser parser)
    {
        parser.ExpectSpace();
        return new UidKey(parser.ReadSequenceSet());
    }

    private static SearchKey Not(ImapParser parser)
    {
        parser.ExpectSpace();
        return new NotKey(ReadKey(parser));
    }

    private static SearchKey Or(ImapParser parser)
    {
        parser.ExpectSpace();
        SearchKey left = ReadKey(parser);
        parser.ExpectSpace();
        return new OrKey(left, ReadKey(parser));
    }
}

/// <summary>
/// Evaluates search criteria against the selected folder. Flags, UIDs and sequence numbers come from the session's snapshot; dates
/// and sizes from the message rows; header criteria from the stored header blocks; BODY/TEXT terms are looked up in the database
/// (case-insensitive LIKE on the stored plain text), so message bodies are never loaded.
/// </summary>
internal sealed class ImapSearchEngine
{
    private const int BatchSize = 500;

    private readonly ImapWork _work;
    private readonly ImapSelection _selection;
    private readonly Dictionary<string, HashSet<long>> _bodyMatches = new(StringComparer.Ordinal);

    public ImapSearchEngine(ImapWork work, ImapSelection selection)
    {
        _work = work;
        _selection = selection;
    }

    /// <summary>The indexes (into the snapshot) of the matching messages, ascending.</summary>
    public async Task<List<int>> SearchAsync(SearchKey key, CancellationToken cancel)
    {
        var needs = new SearchNeeds();
        needs.Collect(key);
        foreach (string term in needs.BodyTerms)
        {
            _bodyMatches[term] = await FindBodyMatchesAsync(term, cancel);
        }

        var result = new List<int>();
        List<int> candidates = Enumerable.Range(0, _selection.Count).Where(i => !_selection.Messages[i].IsExpunged).ToList();
        foreach (int[] batch in candidates.Chunk(BatchSize))
        {
            Dictionary<long, SearchData> data = await LoadAsync(batch, needs, cancel);
            foreach (int index in batch)
            {
                ImapMessage message = _selection.Messages[index];
                if (Matches(key, message, index, data.GetValueOrDefault(message.Id)))
                {
                    result.Add(index);
                }
            }
        }

        return result;
    }

    private bool Matches(SearchKey key, ImapMessage message, int index, SearchData? data)
    {
        return key switch
        {
            AllKey => true,
            NothingKey => false,
            FlagKey flag => ((message.Flags & flag.Flag) != 0) == flag.IsSet,
            KeywordKey keyword => message.Keywords.Contains(keyword.Keyword, StringComparer.OrdinalIgnoreCase) == keyword.IsSet,
            DateKey date => data is not null && CompareDate(date, data),
            SizeKey size => data is not null && (size.Larger ? data.Size > size.Size : data.Size < size.Size),
            HeaderKey header => data is not null && HeaderContains(data, header.Field, header.Value),
            BodyKey body => _bodyMatches[body.Value].Contains(message.Id),
            TextKey text => _bodyMatches[text.Value].Contains(message.Id) || (data is not null && AnyHeaderContains(data, text.Value)),
            UidKey uid => uid.Set.Contains(message.Uid, _selection.MaxUid),
            SequenceKey sequence => sequence.Set.Contains(index + 1, _selection.Count),
            NotKey not => !Matches(not.Key, message, index, data),
            OrKey or => Matches(or.Left, message, index, data) || Matches(or.Right, message, index, data),
            AndKey and => and.Keys.All(k => Matches(k, message, index, data)),
            _ => false,
        };
    }

    private static bool CompareDate(DateKey key, SearchData data)
    {
        DateTime date = key.SentDate ? data.SentDate : data.ReceivedDate.Date;
        return key.Comparison switch
        {
            SearchDateComparison.Before => date < key.Date,
            SearchDateComparison.On => date == key.Date,
            _ => date >= key.Date,
        };
    }

    private static bool HeaderContains(SearchData data, string field, string value)
    {
        foreach (Header header in data.Headers.Where(h => string.Equals(h.Field, field, StringComparison.OrdinalIgnoreCase)))
        {
            if (value.Length == 0 || Contains(header.Value, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyHeaderContains(SearchData data, string value)
        => data.Headers.Any(h => Contains(h.Field + ": " + h.Value, value));

    private static bool Contains(string? text, string value)
        => text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, value, CompareOptions.IgnoreCase) >= 0;

    private async Task<HashSet<long>> FindBodyMatchesAsync(string term, CancellationToken cancel)
    {
        if (term.Length == 0)
        {
            return _selection.Messages.Select(m => m.Id).ToHashSet();
        }

        string pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        long folderId = _selection.FolderId;
        List<long> ids = await _work.Db.MailMessageContents.AsNoTracking()
            .Where(c => c.Message!.FolderId == folderId && c.SearchText != null && EF.Functions.ILike(c.SearchText, pattern, "\\"))
            .Select(c => c.MessageId)
            .ToListAsync(cancel);
        return ids.ToHashSet();
    }

    private async Task<Dictionary<long, SearchData>> LoadAsync(int[] batch, SearchNeeds needs, CancellationToken cancel)
    {
        var data = new Dictionary<long, SearchData>();
        if (!needs.Metadata && !needs.Headers)
        {
            return data;
        }

        long[] ids = batch.Select(i => _selection.Messages[i].Id).ToArray();
        var rows = await _work.Db.MailMessages.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.ReceivedDate, m.SizeBytes })
            .ToListAsync(cancel);
        Dictionary<long, byte[]?> headers = needs.Headers
            ? await _work.Db.MailMessageContents.AsNoTracking()
                .Where(c => ids.Contains(c.MessageId))
                .Select(c => new { c.MessageId, c.HeaderBytes })
                .ToDictionaryAsync(c => c.MessageId, c => c.HeaderBytes, cancel)
            : new Dictionary<long, byte[]?>();

        foreach (var row in rows)
        {
            HeaderList headerList = needs.Headers ? ImapEnvelope.LoadHeaders(headers.GetValueOrDefault(row.Id)) : new HeaderList();
            data[row.Id] = new SearchData(DateTime.SpecifyKind(row.ReceivedDate, DateTimeKind.Utc), row.SizeBytes, headerList);
        }

        return data;
    }

    /// <summary>What a set of criteria needs to be evaluated.</summary>
    private sealed class SearchNeeds
    {
        public bool Metadata { get; private set; }

        public bool Headers { get; private set; }

        public HashSet<string> BodyTerms { get; } = new(StringComparer.Ordinal);

        public void Collect(SearchKey key)
        {
            switch (key)
            {
                case DateKey date:
                    Metadata = true;
                    Headers |= date.SentDate;
                    break;
                case SizeKey:
                    Metadata = true;
                    break;
                case HeaderKey:
                    Headers = true;
                    break;
                case BodyKey body:
                    BodyTerms.Add(body.Value);
                    break;
                case TextKey text:
                    Headers = true;
                    BodyTerms.Add(text.Value);
                    break;
                case NotKey not:
                    Collect(not.Key);
                    break;
                case OrKey or:
                    Collect(or.Left);
                    Collect(or.Right);
                    break;
                case AndKey and:
                    foreach (SearchKey inner in and.Keys)
                    {
                        Collect(inner);
                    }

                    break;
            }
        }
    }

    /// <summary>The facts about one message the criteria look at.</summary>
    private sealed class SearchData
    {
        public SearchData(DateTime receivedDate, long size, HeaderList headers)
        {
            ReceivedDate = receivedDate;
            Size = size;
            Headers = headers;
        }

        public DateTime ReceivedDate { get; }

        public long Size { get; }

        public HeaderList Headers { get; }

        /// <summary>The date of the Date header in its own time zone (RFC 3501: time and zone are disregarded); the arrival date without one.</summary>
        public DateTime SentDate
        {
            get
            {
                string? value = Headers.FirstOrDefault(h => string.Equals(h.Field, "Date", StringComparison.OrdinalIgnoreCase))?.Value;
                return value is not null && DateUtils.TryParse(value, out DateTimeOffset date) ? date.Date : ReceivedDate.Date;
            }
        }
    }
}

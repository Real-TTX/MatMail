using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>
/// A search/list request of the web client. The text is Google-like and understands English and German operator names:
/// <list type="bullet">
/// <item>free words and <c>"exact phrases"</c> (subject, sender, recipients, the first words and the text of the message);</item>
/// <item><c>from:</c> (<c>von:</c>) <c>to:</c> (<c>an:</c>, also the Cc recipients) <c>subject:</c> (<c>betreff:</c>);</item>
/// <item><c>has:attachment</c> (<c>hat:anhang</c>), <c>is:unread|read|starred|answered|draft</c> (<c>ist:ungelesen|gelesen|markiert|beantwortet|entwurf</c>);</item>
/// <item><c>in:inbox|sent|drafts|archive|trash|spam|anywhere</c> or the name or path of a folder (<c>in:Customers/Meier</c>);</item>
/// <item><c>before:</c> <c>after:</c> (yyyy-mm-dd, also dd.mm.yyyy), <c>older_than:</c> <c>newer_than:</c> (<c>7d</c> days, <c>2w</c> weeks, <c>3m</c> months, <c>1y</c> years),
/// <c>larger:</c> <c>smaller:</c> (<c>500k</c>, <c>5M</c>);</item>
/// <item><c>-</c> in front of a term leaves out what matches it, <c>OR</c> (or <c>|</c>) means either, <c>( … )</c> groups, <c>{a b}</c> is "a or b",
/// <c>from:(anna OR ben)</c> and <c>subject:(offer invoice)</c> hand the operator to every word inside.</item>
/// </list>
/// Everything else is a plain word. Terms next to each other must all match.
/// </summary>
public sealed class MailQuery
{
    /// <summary>Longer texts are cut: nobody types a thousand characters, and a pasted page must not become a thousand terms.</summary>
    public const int MaxTextLength = 1000;
    private const int MaxTokens = 120;

    public long MailboxId { get; init; }

    /// <summary>The folder to list. Null: the whole mailbox without Trash and Junk. An <c>in:</c> in the text wins over both.</summary>
    public long? FolderId { get; init; }

    internal SearchNode? Root { get; private set; }

    /// <summary>True when the text asks for something; otherwise the request is a plain listing.</summary>
    public bool IsSearch => Root is not null;

    private bool _hasFolderTerm;
    private readonly Dictionary<string, long[]> _folderIds = new(StringComparer.OrdinalIgnoreCase);

    public static MailQuery Parse(long mailboxId, long? folderId, string? text, DateTime? now = null)
    {
        var query = new MailQuery { MailboxId = mailboxId, FolderId = folderId };
        string source = text is null ? string.Empty : text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        var parser = new Parser(Scan(source), now ?? DateTime.UtcNow);
        query.Root = parser.ParseAll();
        query._hasFolderTerm = parser.HasFolderTerm;
        return query;
    }

    /// <summary>
    /// Looks up the folders that <c>in:</c> names by name or path (case-insensitive; a path is tried first). Call it before
    /// <see cref="Apply"/> when the text may contain such a term; an unknown folder matches nothing.
    /// </summary>
    public async Task ResolveFoldersAsync(MatMailDbContext db, CancellationToken cancel = default)
    {
        string[] wanted = Folders(Root).Where(name => !KnownFolderKinds.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (wanted.Length == 0)
        {
            return;
        }

        var folders = await db.MailFolders.AsNoTracking()
            .Where(f => f.MailboxId == MailboxId)
            .Select(f => new FolderRow(f.Id, f.ParentId, f.Name))
            .ToListAsync(cancel);
        Dictionary<long, FolderRow> byId = folders.ToDictionary(f => f.Id);

        string PathOf(FolderRow folder)
        {
            var parts = new Stack<string>();
            for (FolderRow? current = folder; current is not null; current = current.ParentId is long parent && byId.TryGetValue(parent, out FolderRow? p) ? p : null)
            {
                parts.Push(current.Name);
            }

            return string.Join(FolderService.Separator, parts);
        }

        var paths = folders.Select(f => (f.Id, f.Name, Path: PathOf(f))).ToList();
        foreach (string name in wanted)
        {
            long[] byPath = paths.Where(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Id).ToArray();
            _folderIds[name] = byPath.Length > 0 ? byPath : paths.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Id).ToArray();
        }
    }

    /// <summary>Restricts a message query to what this request asks for. Newest first.</summary>
    public IQueryable<MailMessage> Apply(IQueryable<MailMessage> messages, MatMailDbContext db)
    {
        IQueryable<MailMessage> result = messages.Where(m => m.MailboxId == MailboxId);

        // The text names its folders (in:) or the list is one folder or, without both, everything but Trash and Junk.
        if (!_hasFolderTerm)
        {
            result = FolderId is long folderId
                ? result.Where(m => m.FolderId == folderId)
                : result.Where(m => m.Folder!.Kind != FolderKind.Trash && m.Folder.Kind != FolderKind.Junk);
        }

        if (Root is not null)
        {
            result = result.Where(Predicate(Root, db));
        }

        return result.OrderByDescending(m => m.ReceivedDate).ThenByDescending(m => m.Id);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // From the parsed text to a condition for the database
    // ------------------------------------------------------------------------------------------------------------------

    private Expression<Func<MailMessage, bool>> Predicate(SearchNode node, MatMailDbContext db) => node switch
    {
        AndNode all => all.Parts.Select(p => Predicate(p, db)).Aggregate(AndAlso),
        OrNode either => either.Parts.Select(p => Predicate(p, db)).Aggregate(OrElse),
        NotNode negated => Negate(Predicate(negated.Inner, db)),
        TermNode term => Term(term, db),
        _ => m => true,
    };

    private Expression<Func<MailMessage, bool>> Term(TermNode term, MatMailDbContext db)
    {
        switch (term.Field)
        {
            case SearchField.Word:
            {
                string pattern = Pattern(term.Text);
                return m => EF.Functions.ILike(m.Subject, pattern) || EF.Functions.ILike(m.FromName, pattern) || EF.Functions.ILike(m.FromAddress, pattern)
                            || EF.Functions.ILike(m.ToSummary, pattern) || EF.Functions.ILike(m.Preview, pattern)
                            || db.MailMessageContents.Any(c => c.MessageId == m.Id && c.SearchText != null && EF.Functions.ILike(c.SearchText, pattern));
            }

            case SearchField.From:
            {
                string pattern = Pattern(term.Text);
                return m => EF.Functions.ILike(m.FromName, pattern) || EF.Functions.ILike(m.FromAddress, pattern);
            }

            case SearchField.To:
            {
                string pattern = Pattern(term.Text);
                return m => EF.Functions.ILike(m.ToSummary, pattern);
            }

            case SearchField.Subject:
            {
                string pattern = Pattern(term.Text);
                return m => EF.Functions.ILike(m.Subject, pattern);
            }

            case SearchField.Attachment: return m => m.HasAttachments;
            case SearchField.Unread: return m => !m.IsRead;
            case SearchField.Read: return m => m.IsRead;
            case SearchField.Starred: return m => m.IsStarred;
            case SearchField.Answered: return m => m.IsAnswered;
            case SearchField.Draft: return m => m.IsDraft;
            case SearchField.Folder: return FolderPredicate(term.Text);

            case SearchField.Before:
            case SearchField.OlderThan:
            {
                DateTime limit = term.Date!.Value;
                return m => m.ReceivedDate < limit;
            }

            case SearchField.After:
            case SearchField.NewerThan:
            {
                DateTime limit = term.Date!.Value;
                return m => m.ReceivedDate >= limit;
            }

            case SearchField.Larger:
            {
                long bytes = term.Number!.Value;
                return m => m.SizeBytes > bytes;
            }

            case SearchField.Smaller:
            {
                long bytes = term.Number!.Value;
                return m => m.SizeBytes < bytes;
            }

            default: return m => true;
        }
    }

    private Expression<Func<MailMessage, bool>> FolderPredicate(string name)
    {
        if (KnownFolderKinds.TryGetValue(name, out FolderKind? kind))
        {
            if (kind is null)
            {
                return m => true;   // anywhere
            }

            FolderKind wanted = kind.Value;
            return m => m.Folder!.Kind == wanted;
        }

        if (_folderIds.TryGetValue(name, out long[]? ids) && ids.Length > 0)
        {
            return m => ids.Contains(m.FolderId);
        }

        return m => false;
    }

    private static Expression<Func<MailMessage, bool>> AndAlso(Expression<Func<MailMessage, bool>> left, Expression<Func<MailMessage, bool>> right)
        => Combine(left, right, Expression.AndAlso);

    private static Expression<Func<MailMessage, bool>> OrElse(Expression<Func<MailMessage, bool>> left, Expression<Func<MailMessage, bool>> right)
        => Combine(left, right, Expression.OrElse);

    private static Expression<Func<MailMessage, bool>> Negate(Expression<Func<MailMessage, bool>> inner)
        => Expression.Lambda<Func<MailMessage, bool>>(Expression.Not(inner.Body), inner.Parameters);

    private static Expression<Func<MailMessage, bool>> Combine(
        Expression<Func<MailMessage, bool>> left, Expression<Func<MailMessage, bool>> right, Func<Expression, Expression, BinaryExpression> join)
    {
        ParameterExpression parameter = left.Parameters[0];
        Expression rebound = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<MailMessage, bool>>(join(left.Body, rebound), parameter);
    }

    private sealed class ParameterRebinder(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : base.VisitParameter(node);
    }

    private static string Pattern(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private sealed record FolderRow(long Id, long? ParentId, string Name);

    private static IEnumerable<string> Folders(SearchNode? node) => node switch
    {
        AndNode all => all.Parts.SelectMany(Folders),
        OrNode either => either.Parts.SelectMany(Folders),
        NotNode negated => Folders(negated.Inner),
        TermNode { Field: SearchField.Folder } term => new[] { term.Text },
        _ => Array.Empty<string>(),
    };

    /// <summary>The folders that <c>in:</c> knows by name in both languages; null = everywhere.</summary>
    private static readonly Dictionary<string, FolderKind?> KnownFolderKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inbox"] = FolderKind.Inbox, ["posteingang"] = FolderKind.Inbox,
        ["sent"] = FolderKind.Sent, ["gesendet"] = FolderKind.Sent,
        ["drafts"] = FolderKind.Drafts, ["draft"] = FolderKind.Drafts, ["entwürfe"] = FolderKind.Drafts, ["entwuerfe"] = FolderKind.Drafts, ["entwurf"] = FolderKind.Drafts,
        ["archive"] = FolderKind.Archive, ["archiv"] = FolderKind.Archive,
        ["trash"] = FolderKind.Trash, ["bin"] = FolderKind.Trash, ["papierkorb"] = FolderKind.Trash,
        ["spam"] = FolderKind.Junk, ["junk"] = FolderKind.Junk,
        ["anywhere"] = null, ["all"] = null, ["überall"] = null, ["ueberall"] = null,
    };

    // ------------------------------------------------------------------------------------------------------------------
    // The text, split into tokens
    // ------------------------------------------------------------------------------------------------------------------

    private enum TokenKind { Word, Phrase, KeyOpen, LeftParen, RightParen, LeftBrace, RightBrace, Or, Not }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length && tokens.Count < MaxTokens)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenKind.LeftParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RightParen, ")")); i++; continue;
                case '{': tokens.Add(new Token(TokenKind.LeftBrace, "{")); i++; continue;
                case '}': tokens.Add(new Token(TokenKind.RightBrace, "}")); i++; continue;
                case '|': tokens.Add(new Token(TokenKind.Or, "|")); i++; continue;
                case '"':
                {
                    int end = text.IndexOf('"', i + 1);
                    tokens.Add(new Token(TokenKind.Phrase, end < 0 ? text[(i + 1)..] : text[(i + 1)..end]));
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }

                case '-' when i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]):
                    tokens.Add(new Token(TokenKind.Not, "-"));
                    i++;
                    continue;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && "(){}\"|".IndexOf(text[i]) < 0)
            {
                i++;
            }

            string word = text[start..i];
            if (word.Length > 1 && word[^1] == ':' && i < text.Length && (text[i] == '"' || text[i] == '('))
            {
                tokens.Add(new Token(TokenKind.KeyOpen, word[..^1]));   // from:"Max Mustermann" · from:(anna OR ben)
            }
            else if (word is "OR" or "ODER")
            {
                tokens.Add(new Token(TokenKind.Or, word));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Word, word));
            }
        }

        return tokens;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The tokens, turned into a tree: OR binds weaker than the blank between terms
    // ------------------------------------------------------------------------------------------------------------------

    private sealed class Parser(List<Token> tokens, DateTime now)
    {
        private int _index;
        private int _notDepth;   // inside a term that is left out ("-in:sent") a folder does not say where to look
        private string? _key;    // inside key:( … ) every plain word belongs to the key

        public bool HasFolderTerm { get; private set; }

        private Token? Peek => _index < tokens.Count ? tokens[_index] : null;

        public SearchNode? ParseAll()
        {
            SearchNode? result = ParseOr();
            while (_index < tokens.Count)
            {
                _index++;   // a closing bracket without an opening one
                SearchNode? more = ParseOr();
                result = more is null ? result : result is null ? more : new AndNode(new[] { result, more });
            }

            return result;
        }

        private SearchNode? ParseOr()
        {
            var parts = new List<SearchNode>();
            SearchNode? first = ParseAnd();
            if (first is not null)
            {
                parts.Add(first);
            }

            while (Peek is { Kind: TokenKind.Or })
            {
                _index++;
                SearchNode? next = ParseAnd();
                if (next is not null)
                {
                    parts.Add(next);
                }
            }

            return OneOf(parts);
        }

        private SearchNode? ParseAnd()
        {
            var parts = new List<SearchNode>();
            while (Peek is { } token && token.Kind is not (TokenKind.Or or TokenKind.RightParen or TokenKind.RightBrace))
            {
                SearchNode? node = ParseUnary();
                if (node is not null)
                {
                    parts.Add(node);
                }
            }

            return parts.Count switch { 0 => null, 1 => parts[0], _ => new AndNode(parts) };
        }

        private SearchNode? ParseUnary()
        {
            if (Peek is { Kind: TokenKind.Not })
            {
                _index++;
                _notDepth++;
                SearchNode? inner = ParseUnary();
                _notDepth--;
                return inner is null ? null : new NotNode(inner);
            }

            return ParsePrimary();
        }

        private SearchNode? ParsePrimary()
        {
            Token token = tokens[_index++];
            switch (token.Kind)
            {
                case TokenKind.LeftParen:
                {
                    SearchNode? inner = ParseOr();
                    if (Peek is { Kind: TokenKind.RightParen })
                    {
                        _index++;
                    }

                    return inner;
                }

                case TokenKind.LeftBrace:
                {
                    var parts = new List<SearchNode>();
                    while (Peek is { } next && next.Kind != TokenKind.RightBrace)
                    {
                        if (next.Kind == TokenKind.Or)
                        {
                            _index++;
                            continue;
                        }

                        SearchNode? node = ParseUnary();
                        if (node is not null)
                        {
                            parts.Add(node);
                        }
                    }

                    if (Peek is { Kind: TokenKind.RightBrace })
                    {
                        _index++;
                    }

                    return OneOf(parts);
                }

                case TokenKind.Word: return FromWord(token.Text);
                case TokenKind.Phrase: return Leaf(token.Text);
                case TokenKind.KeyOpen: return FromKeyOpen(token.Text);
                default: return null;
            }
        }

        private SearchNode? FromKeyOpen(string key)
        {
            if (Peek is { Kind: TokenKind.Phrase } phrase)
            {
                _index++;
                return Keyed(key, phrase.Text) ?? new TermNode(SearchField.Word, key + ":" + phrase.Text);
            }

            if (Peek is { Kind: TokenKind.LeftParen })
            {
                _index++;
                string? saved = _key;
                _key = Operators.ContainsKey(NormalizeKey(key)) ? NormalizeKey(key) : null;
                SearchNode? inner = ParseOr();
                _key = saved;
                if (Peek is { Kind: TokenKind.RightParen })
                {
                    _index++;
                }

                return inner;
            }

            return null;
        }

        private SearchNode? FromWord(string word)
        {
            if (word == "-")
            {
                return null;
            }

            if (_key is not null)
            {
                return Leaf(word);
            }

            int colon = word.IndexOf(':');
            if (colon > 0 && colon < word.Length - 1)
            {
                SearchNode? keyed = Keyed(word[..colon], word[(colon + 1)..]);
                if (keyed is not null)
                {
                    return keyed;
                }
            }
            else if (colon == word.Length - 1 && Operators.ContainsKey(NormalizeKey(word[..colon])))
            {
                return null;   // "from:" while somebody is still typing: nothing to look for yet
            }

            return new TermNode(SearchField.Word, word);
        }

        /// <summary>A word or a phrase: inside <c>key:( … )</c> it is a value of the key, elsewhere free text.</summary>
        private SearchNode? Leaf(string text)
        {
            if (text.Trim().Length == 0)
            {
                return null;
            }

            return _key is not null ? Keyed(_key, text) ?? new TermNode(SearchField.Word, text) : new TermNode(SearchField.Word, text);
        }

        private static SearchNode? OneOf(List<SearchNode> parts) => parts.Count switch { 0 => null, 1 => parts[0], _ => new OrNode(parts) };

        /// <summary>The term for <c>key:value</c>, or null when the key is no operator or the value makes no sense for it.</summary>
        private SearchNode? Keyed(string rawKey, string rawValue)
        {
            string value = rawValue.Trim().Trim('"').Trim();
            if (value.Length == 0)
            {
                return null;
            }

            if (!Operators.TryGetValue(NormalizeKey(rawKey), out string? key))
            {
                return null;
            }

            switch (key)
            {
                case "from": return new TermNode(SearchField.From, value);
                case "to": return new TermNode(SearchField.To, value);
                case "subject": return new TermNode(SearchField.Subject, value);
                case "has": return AttachmentWords.Contains(value) ? new TermNode(SearchField.Attachment, "attachment") : null;
                case "is":
                    return value.ToLowerInvariant() switch
                    {
                        "unread" or "ungelesen" => new TermNode(SearchField.Unread, "unread"),
                        "read" or "gelesen" => new TermNode(SearchField.Read, "read"),
                        "starred" or "markiert" or "stern" => new TermNode(SearchField.Starred, "starred"),
                        "answered" or "beantwortet" => new TermNode(SearchField.Answered, "answered"),
                        "draft" or "entwurf" => new TermNode(SearchField.Draft, "draft"),
                        _ => null,
                    };
                case "in":
                    HasFolderTerm |= _notDepth == 0;   // Trash and Spam are searched when a folder asks for them, not when one is left out
                    return new TermNode(SearchField.Folder, value);
                case "before": return TryDate(value, out DateTime before) ? new TermNode(SearchField.Before, value) { Date = before } : null;
                case "after": return TryDate(value, out DateTime after) ? new TermNode(SearchField.After, value) { Date = after } : null;
                case "older_than": return TryAgo(value, now, out DateTime older) ? new TermNode(SearchField.OlderThan, value) { Date = older } : null;
                case "newer_than": return TryAgo(value, now, out DateTime newer) ? new TermNode(SearchField.NewerThan, value) { Date = newer } : null;
                case "larger": return TryBytes(value, out long large) ? new TermNode(SearchField.Larger, value) { Number = large } : null;
                case "smaller": return TryBytes(value, out long small) ? new TermNode(SearchField.Smaller, value) { Number = small } : null;
                default: return null;
            }
        }
    }

    /// <summary>The operators (canonical names) by every name that is accepted for them.</summary>
    private static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["from"] = "from", ["to"] = "to", ["subject"] = "subject", ["has"] = "has", ["is"] = "is", ["in"] = "in",
        ["before"] = "before", ["after"] = "after", ["older_than"] = "older_than", ["newer_than"] = "newer_than", ["larger"] = "larger", ["smaller"] = "smaller",
    };

    private static readonly Dictionary<string, string> GermanKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["von"] = "from", ["an"] = "to", ["betreff"] = "subject", ["hat"] = "has", ["ist"] = "is", ["ordner"] = "in",
        ["vor"] = "before", ["nach"] = "after",
        ["älter_als"] = "older_than", ["aelter_als"] = "older_than", ["neuer_als"] = "newer_than",
        ["größer"] = "larger", ["groesser"] = "larger", ["grösser"] = "larger", ["kleiner"] = "smaller",
    };

    private static string NormalizeKey(string key)
    {
        string lower = key.Trim().ToLowerInvariant();
        return GermanKeys.TryGetValue(lower, out string? english) ? english : lower;
    }

    private static readonly HashSet<string> AttachmentWords = new(StringComparer.OrdinalIgnoreCase) { "attachment", "attachments", "attach", "anhang", "anhänge", "anhaenge" };

    private static bool TryDate(string value, out DateTime date)
        => DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);

    /// <summary>"7d" (days), "2w" (weeks), "3m" (months), "1y" (years); a bare number is days. The result is that long before now.</summary>
    private static bool TryAgo(string value, DateTime now, out DateTime moment)
    {
        moment = default;
        Match match = Regex.Match(value, @"^(\d{1,4})\s*([dwmy]?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        int amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        moment = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "w" => now.AddDays(-7.0 * amount),
            "m" => now.AddMonths(-amount),
            "y" => now.AddYears(-amount),
            _ => now.AddDays(-amount),
        };
        return true;
    }

    /// <summary>"500k", "5M", "1.5G", "2000000" (bytes); units are 1024 based.</summary>
    private static bool TryBytes(string value, out long bytes)
    {
        bytes = 0;
        Match match = Regex.Match(value, @"^(\d+(?:[.,]\d+)?)\s*(k|kb|m|mb|g|gb)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return false;
        }

        double factor = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" or "kb" => 1024.0,
            "m" or "mb" => 1024.0 * 1024,
            "g" or "gb" => 1024.0 * 1024 * 1024,
            _ => 1.0,
        };
        bytes = (long)Math.Min(number * factor, long.MaxValue / 2.0);
        return true;
    }

    /// <summary>The tree as text, for tests and logs: <c>AND(from:anna, OR(word:a, word:b), NOT(is:unread))</c>.</summary>
    internal string Describe() => Describe(Root);

    private static string Describe(SearchNode? node) => node switch
    {
        null => string.Empty,
        AndNode all => "AND(" + string.Join(", ", all.Parts.Select(Describe)) + ")",
        OrNode either => "OR(" + string.Join(", ", either.Parts.Select(Describe)) + ")",
        NotNode negated => "NOT(" + Describe(negated.Inner) + ")",
        TermNode term => TermName(term.Field) + ":" + term.Text,
        _ => string.Empty,
    };

    private static string TermName(SearchField field) => field switch
    {
        SearchField.Word => "word",
        SearchField.From => "from",
        SearchField.To => "to",
        SearchField.Subject => "subject",
        SearchField.Attachment => "has",
        SearchField.Unread or SearchField.Read or SearchField.Starred or SearchField.Answered or SearchField.Draft => "is",
        SearchField.Folder => "in",
        SearchField.Before => "before",
        SearchField.After => "after",
        SearchField.OlderThan => "older_than",
        SearchField.NewerThan => "newer_than",
        SearchField.Larger => "larger",
        SearchField.Smaller => "smaller",
        _ => "?",
    };
}

internal enum SearchField { Word, From, To, Subject, Attachment, Unread, Read, Starred, Answered, Draft, Folder, Before, After, OlderThan, NewerThan, Larger, Smaller }

internal abstract record SearchNode;

internal sealed record AndNode(IReadOnlyList<SearchNode> Parts) : SearchNode;

internal sealed record OrNode(IReadOnlyList<SearchNode> Parts) : SearchNode;

internal sealed record NotNode(SearchNode Inner) : SearchNode;

internal sealed record TermNode(SearchField Field, string Text) : SearchNode
{
    public DateTime? Date { get; init; }
    public long? Number { get; init; }
}

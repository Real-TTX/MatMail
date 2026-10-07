using System.Globalization;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>
/// A search/list request of the web client. The text is Gmail-like: free words plus operators
/// <c>from:</c> <c>to:</c> <c>subject:</c> <c>has:attachment</c> <c>is:unread|read|starred</c> <c>before:</c> <c>after:</c> (dates as yyyy-mm-dd);
/// quotes group words (<c>subject:"two words"</c>).
/// </summary>
public sealed class MailQuery
{
    public long MailboxId { get; init; }

    /// <summary>The folder to list. Null with a search text = search the whole mailbox (without Trash and Junk).</summary>
    public long? FolderId { get; init; }

    public List<string> Words { get; } = new();
    public List<string> From { get; } = new();
    public List<string> To { get; } = new();
    public List<string> Subject { get; } = new();
    public bool? Unread { get; set; }
    public bool? Starred { get; set; }
    public bool? HasAttachment { get; set; }
    public DateTime? Before { get; set; }
    public DateTime? After { get; set; }

    public bool IsSearch => Words.Count + From.Count + To.Count + Subject.Count > 0 || Unread is not null || Starred is not null
                            || HasAttachment is not null || Before is not null || After is not null;

    public static MailQuery Parse(long mailboxId, long? folderId, string? text)
    {
        var query = new MailQuery { MailboxId = mailboxId, FolderId = folderId };
        foreach (string token in Tokenize(text ?? string.Empty))
        {
            int colon = token.IndexOf(':');
            string key = colon > 0 ? token[..colon].ToLowerInvariant() : string.Empty;
            string value = colon > 0 ? token[(colon + 1)..].Trim('"') : token.Trim('"');
            if (value.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "from": query.From.Add(value); break;
                case "to": query.To.Add(value); break;
                case "subject": query.Subject.Add(value); break;
                case "has" when value.StartsWith("attach", StringComparison.OrdinalIgnoreCase): query.HasAttachment = true; break;
                case "is" when value.Equals("unread", StringComparison.OrdinalIgnoreCase): query.Unread = true; break;
                case "is" when value.Equals("read", StringComparison.OrdinalIgnoreCase): query.Unread = false; break;
                case "is" when value.Equals("starred", StringComparison.OrdinalIgnoreCase): query.Starred = true; break;
                case "before" when TryDate(value, out DateTime before): query.Before = before; break;
                case "after" when TryDate(value, out DateTime after): query.After = after; break;
                default: query.Words.Add(token.Trim('"')); break;
            }
        }

        return query;
    }

    /// <summary>Restricts a message query to what this request asks for. Newest first.</summary>
    public IQueryable<MailMessage> Apply(IQueryable<MailMessage> messages, MatMailDbContext db)
    {
        IQueryable<MailMessage> result = messages.Where(m => m.MailboxId == MailboxId);

        if (FolderId is long folderId)
        {
            result = result.Where(m => m.FolderId == folderId);
        }
        else
        {
            result = result.Where(m => m.Folder!.Kind != FolderKind.Trash && m.Folder.Kind != FolderKind.Junk);
        }

        foreach (string word in Words)
        {
            string pattern = Pattern(word);
            result = result.Where(m =>
                EF.Functions.ILike(m.Subject, pattern) || EF.Functions.ILike(m.FromName, pattern) || EF.Functions.ILike(m.FromAddress, pattern)
                || EF.Functions.ILike(m.ToSummary, pattern) || EF.Functions.ILike(m.Preview, pattern)
                || db.MailMessageContents.Any(c => c.MessageId == m.Id && c.SearchText != null && EF.Functions.ILike(c.SearchText, pattern)));
        }

        foreach (string value in From)
        {
            string pattern = Pattern(value);
            result = result.Where(m => EF.Functions.ILike(m.FromName, pattern) || EF.Functions.ILike(m.FromAddress, pattern));
        }

        foreach (string value in To)
        {
            string pattern = Pattern(value);
            result = result.Where(m => EF.Functions.ILike(m.ToSummary, pattern));
        }

        foreach (string value in Subject)
        {
            string pattern = Pattern(value);
            result = result.Where(m => EF.Functions.ILike(m.Subject, pattern));
        }

        if (Unread is bool unread)
        {
            result = result.Where(m => m.IsRead != unread);
        }

        if (Starred is true)
        {
            result = result.Where(m => m.IsStarred);
        }

        if (HasAttachment is true)
        {
            result = result.Where(m => m.HasAttachments);
        }

        if (Before is DateTime before)
        {
            result = result.Where(m => m.ReceivedDate < before);
        }

        if (After is DateTime after)
        {
            result = result.Where(m => m.ReceivedDate >= after);
        }

        return result.OrderByDescending(m => m.ReceivedDate).ThenByDescending(m => m.Id);
    }

    private static string Pattern(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static bool TryDate(string value, out DateTime date)
    {
        bool ok = DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
        return ok;
    }

    /// <summary>Splits on spaces; a quoted part (also after "key:") stays together.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}

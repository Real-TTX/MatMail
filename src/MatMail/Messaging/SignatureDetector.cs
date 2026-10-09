using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace MatMail.Messaging;

/// <summary>
/// Whether a message already carries the signature of its sender, the way the mail program of the sender puts it there. The server
/// adds its own signature only to messages that have none (<c>Signature.AddOnServer</c>): a second one under the first is worse than
/// none. Only what the sender wrote counts – the quoted original of a reply (and its signature) does not, and neither does the
/// line of advertising that a phone puts under a message ("Sent from my iPhone"): that one is no signature, the company wants its
/// own under it.
/// </summary>
public static partial class SignatureDetector
{
    /// <summary>
    /// What mail programs mark the signature they insert with: Thunderbird (<c>moz-signature</c>), Gmail (<c>gmail_signature</c>), Outlook and
    /// Outlook on the web (the id <c>Signature</c>), Roundcube, Evolution and the web client of MatMail itself. The <c>x_Signature</c> that
    /// Outlook on the web leaves in a quoted message is not on the list on purpose: it is somebody else's.
    /// </summary>
    private const string Markers = ".mm-signature, .moz-signature, .gmail_signature, .signature, #Signature, #_rc_sig, [id='-x-evo-signature'], [data-smartmail='gmail_signature']";

    /// <summary>The parts of an HTML message that are the quoted original of a reply or a forward (Thunderbird, Apple Mail, Gmail, Yahoo).</summary>
    private const string QuotedParts = "blockquote, .gmail_quote, .yahoo_quoted, .moz-forward-container";

    /// <summary>Whether the HTML of a message carries a signature of the sender (a marker of a mail program, or the usual separator).</summary>
    public static bool HtmlCarries(string html)
    {
        IHtmlDocument document = Written(html);
        foreach (IElement element in document.QuerySelectorAll(Markers))
        {
            if (IsSignature(element.TextContent, element.QuerySelector("img") is not null))
            {
                return true;
            }
        }

        return PlainCarries(HtmlText.ToPlainText(document.Body?.InnerHtml));
    }

    /// <summary>The text of what the sender wrote: the HTML without the quoted original.</summary>
    public static string WrittenText(string html) => HtmlText.ToPlainText(Written(html).Body?.InnerHtml);

    /// <summary>
    /// Whether a text carries a signature: the separator of RFC 3676 (a line of two dashes, usually with a space after them) followed by
    /// a short block that is not quoted. Quoted lines and everything below the header of a quoted original do not count.
    /// </summary>
    public static bool PlainCarries(string text)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        int end = StartOfQuotedOriginal(lines);
        for (int i = end - 1; i >= 0; i--)
        {
            if (lines[i].TrimEnd() != "--")
            {
                continue;
            }

            // The last separator is the one of the signature; what follows it, up to a quote, is the signature if it is short.
            var block = new List<string>();
            for (int j = i + 1; j < end && !IsQuoted(lines[j]); j++)
            {
                block.Add(lines[j].Trim());
            }

            while (block.Count > 0 && block[^1].Length == 0)
            {
                block.RemoveAt(block.Count - 1);
            }

            return block.Count is > 0 and <= MaxSignatureLines && IsSignature(string.Join('\n', block), hasPicture: false);
        }

        return false;
    }

    private const int MaxSignatureLines = 20;

    /// <summary>The HTML as written by the sender: parsed, the quoted original of a reply or forward taken out.</summary>
    private static IHtmlDocument Written(string html)
    {
        IHtmlDocument document = new HtmlParser().ParseDocument(html);
        foreach (IElement quoted in document.QuerySelectorAll(QuotedParts).ToList())
        {
            quoted.Remove();
        }

        // Outlook does not quote: it puts a header in front of the original, and everything below it is the original.
        foreach (IElement start in document.QuerySelectorAll("#divRplyFwdMsg, #appendonsend, .OutlookMessageHeader, [style]").Where(IsOutlookQuoteStart).ToList())
        {
            RemoveWithFollowing(start);
        }

        return document;
    }

    private static bool IsOutlookQuoteStart(IElement element)
        => element.Id is "divRplyFwdMsg" or "appendonsend"
           || element.ClassList.Contains("OutlookMessageHeader")
           || OutlookHeaderBorder().IsMatch(element.GetAttribute("style") ?? string.Empty);

    /// <summary>Takes the element and everything that comes after it in the document out.</summary>
    private static void RemoveWithFollowing(IElement start)
    {
        INode? node = start;
        while (node is not null and not IHtmlBodyElement && node.Parent is { } parent)
        {
            while (node.NextSibling is { } next)
            {
                parent.RemoveChild(next);
            }

            node = parent;
        }

        start.Remove();
    }

    /// <summary>Something is a signature when it says something, and is not a tagline that a phone or an app puts under every message.</summary>
    private static bool IsSignature(string text, bool hasPicture)
    {
        string collapsed = Whitespace().Replace(text, " ").Trim();
        if (collapsed.Length == 0)
        {
            return hasPicture;
        }

        return !(collapsed.Length <= 100 && Tagline().IsMatch(collapsed));
    }

    private static bool IsQuoted(string line) => line.TrimStart().StartsWith('>');

    /// <summary>The line at which the quoted original of a plain-text message begins, else the number of lines.</summary>
    private static int StartOfQuotedOriginal(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (OriginalBanner().IsMatch(line) || (line.Length >= 20 && line.All(c => c == '_') && NextLine(lines, i).StartsWith("From:", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }

            // Outlook's header block: From / Sent (or the German names), one after the other.
            if (HeaderFrom().IsMatch(line) && HeaderSent().IsMatch(string.Join('\n', lines.Skip(i + 1).Take(3))))
            {
                return i;
            }
        }

        return lines.Length;
    }

    private static string NextLine(string[] lines, int index)
    {
        for (int i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                return lines[i].Trim();
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private const string Devices = "iphone|ipad|ipod|android|galaxy|samsung|pixel|huawei|xiaomi|oneplus|blackberry|windows phone|outlook|yahoo|mail|gmail|proton|handy|smartphone|tablet|phone|mobile|device";

    /// <summary>"Sent from my iPhone", "Get Outlook for Android", "Gesendet von meinem iPad": what the program says about itself, not about the sender.</summary>
    [GeneratedRegex(
        @"^(get outlook for|(sent|gesendet|versendet|envoy[ée]|enviado|inviato) (from|via|with|using|von|mit|aus|über|de|depuis|avec|desde|con|da|dal) (my |the |meinem |meiner |der |dem |mon |ma |mi |il mio )?(" + Devices + @")|von meinem .+ (gesendet|versendet)$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Tagline();

    [GeneratedRegex(@"border-top:\s*solid\s*#(E1E1E1|B5C4DF)", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookHeaderBorder();

    [GeneratedRegex(@"^[-_= ]{4,}\s*(Original[- ]?(Message|Nachricht)|Ursprüngliche Nachricht|Forwarded message|Weitergeleitete Nachricht|Message d'origine)\s*[-_= ]{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalBanner();

    [GeneratedRegex(@"^(From|Von|De):\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFrom();

    [GeneratedRegex(@"^(Sent|Gesendet|Date|Datum|Envoy[ée]):\s+\S", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderSent();
}

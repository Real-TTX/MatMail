using System.Text;
using MimeKit.Text;

namespace MatMail.Messaging;

/// <summary>Turns an HTML mail body into readable plain text (for previews, search and the plain-text footer).</summary>
public static class HtmlText
{
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = new StringBuilder(html.Length / 2);
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        int hidden = 0;

        while (tokenizer.ReadNextToken(out HtmlToken? token))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Tag:
                    var tag = (HtmlTagToken)token;
                    if (tag.Id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Title or HtmlTagId.Head)
                    {
                        hidden = Math.Max(0, hidden + (tag.IsEndTag ? -1 : 1));
                    }
                    else if (IsLineBreak(tag.Id))
                    {
                        text.Append('\n');
                    }
                    else if ((tag.Id == HtmlTagId.TD || tag.Id == HtmlTagId.TH) && !tag.IsEndTag)
                    {
                        text.Append(' ');
                    }

                    break;

                case HtmlTokenKind.Data when hidden == 0:
                    text.Append(((HtmlDataToken)token).Data);
                    break;
            }
        }

        return text.ToString();
    }

    private static bool IsLineBreak(HtmlTagId id) => id is
        HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div or HtmlTagId.LI or HtmlTagId.TR or HtmlTagId.BlockQuote or HtmlTagId.Pre
        or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6 or HtmlTagId.UL or HtmlTagId.OL or HtmlTagId.Table;
}

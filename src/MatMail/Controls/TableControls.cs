using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;

namespace MatMail.Controls;

/// <summary>
/// A list of items (LIST-05): the toolbar, the applied tools, the table or the empty state, the pagination and the list actions, in this order whatever the order
/// of the markup is. Everything of one list goes inside: <c>mm-toolbar</c>, <c>mm-table</c>, <c>mm-pagination</c>, <c>mm-list-actions</c>.
/// </summary>
[HtmlTargetElement("mm-list")]
public sealed class ListTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-list");
    }
}

/// <summary>
/// The one table of the application: renders <c>&lt;div class="mat-table-wrap"&gt;&lt;table class="mat-table"&gt;…</c> around the &lt;thead&gt;/&lt;tbody&gt;
/// given as child content, or an empty state when <c>item-count</c> is 0 (LIST-11). Every &lt;td&gt; gets <c>data-label</c> from the heading of its column
/// (TABLE-02: on a phone a row becomes a card), a cell whose heading is empty gets none. Put <c>mat-table__cell-actions</c> on the last cell for the row actions,
/// <c>mat-num</c> on numbers, <c>mat-hide-sm</c> on secondary columns. A row with <c>data-row-href</c> opens on a click (matmail.js); the title in it is the link (LIST-09).
/// Several tables may share a page: nothing in here is id based.
/// Usage: &lt;mm-table item-count="Model.Rows.Count" empty-text="No records."&gt;&lt;thead&gt;…&lt;/mm-table&gt;
/// </summary>
[HtmlTargetElement("mm-table")]
public sealed partial class TableTagHelper(IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("item-count")]
    public int ItemCount { get; set; }

    [HtmlAttributeName("empty-text")]
    public string? EmptyText { get; set; }

    [HtmlAttributeName("empty-icon")]
    public string EmptyIcon { get; set; } = "inbox";

    /// <summary>A name for screen readers (the caption of the table).</summary>
    [HtmlAttributeName("caption")]
    public string? Caption { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (ItemCount <= 0)
        {
            output.Attributes.SetAttribute("class", "mat-empty");
            output.Content.SetHtmlContent(
                $"<span class=\"mat-empty__icon\">{toolkit.IconMarkup(EmptyIcon)}</span>" +
                $"<div class=\"mat-empty__title\">{HtmlEncoder.Default.Encode(EmptyText ?? l["No records found."].Value)}</div>");
            return;
        }

        output.Attributes.SetAttribute("class", "mat-table-wrap");
        string children = (await output.GetChildContentAsync()).GetContent();
        output.Content.SetHtmlContent("<table class=\"mat-table\">");
        if (!string.IsNullOrEmpty(Caption))
        {
            output.Content.AppendHtml($"<caption class=\"mat-sr-only\">{HtmlEncoder.Default.Encode(Caption)}</caption>");
        }

        output.Content.AppendHtml(AddCellLabels(children));
        output.Content.AppendHtml("</table>");
    }

    /// <summary>Gives every cell of the body the name of its column (the text of the heading above it), unless it has a label already.</summary>
    internal static string AddCellLabels(string html)
    {
        Match head = HeadRegex().Match(html);
        if (!head.Success)
        {
            return html;
        }

        var labels = new List<string>();
        foreach (Match cell in HeadCellRegex().Matches(head.Value))
        {
            int span = ColSpan(cell.Groups[1].Value);
            // A heading that only a screen reader sees (the column of the row actions) names no line of the card.
            string text = cell.Groups[2].Value.Contains("mat-sr-only", StringComparison.Ordinal) ? string.Empty : WebUtility.HtmlDecode(TagRegex().Replace(cell.Groups[2].Value, string.Empty)).Trim();
            for (int i = 0; i < span; i++)
            {
                labels.Add(i == 0 ? text : string.Empty);
            }
        }

        int bodyStart = head.Index + head.Length;
        string body = RowRegex().Replace(html[bodyStart..], row =>
        {
            int column = 0;
            string inner = CellRegex().Replace(row.Groups[2].Value, cell =>
            {
                string attributes = cell.Groups[1].Value;
                int index = column;
                column += ColSpan(attributes);
                if (attributes.Contains("data-label", StringComparison.OrdinalIgnoreCase) || index >= labels.Count || labels[index].Length == 0)
                {
                    return cell.Value;
                }

                return $"<td{attributes} data-label=\"{WebUtility.HtmlEncode(labels[index])}\">";
            });
            return $"<tr{row.Groups[1].Value}>{inner}</tr>";
        });
        return html[..bodyStart] + body;
    }

    private static int ColSpan(string attributes)
    {
        Match span = ColSpanRegex().Match(attributes);
        return span.Success && int.TryParse(span.Groups[1].Value, out int n) && n > 0 ? n : 1;
    }

    [GeneratedRegex(@"<thead\b[^>]*>.*?</thead>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"<th\b([^>]*)>(.*?)</th>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadCellRegex();

    [GeneratedRegex(@"<tr\b([^>]*)>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<td\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"colspan\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColSpanRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}

/// <summary>
/// Pagination directly under the table (LIST-06): "first to last of total", previous/next and a window of page numbers. All other query-string
/// parameters (search, filters, sort) are kept; only the page parameter changes. Renders nothing for a single page (CODE-06).
/// Several paginations on one page use different <c>page-param</c> names.
/// Usage: &lt;mm-pagination current-page="Model.PageNumber" total-pages="Model.TotalPages" total-count="Model.TotalCount" page-size="20" /&gt;
/// </summary>
[HtmlTargetElement("mm-pagination", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PaginationTagHelper(IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("current-page")]
    public int CurrentPage { get; set; } = 1;

    [HtmlAttributeName("total-pages")]
    public int TotalPages { get; set; }

    [HtmlAttributeName("total-count")]
    public int? TotalCount { get; set; }

    [HtmlAttributeName("page-size")]
    public int PageSize { get; set; }

    [HtmlAttributeName("page-param")]
    public string PageParam { get; set; } = "PageNumber";

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (TotalPages <= 1)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-pager");
        output.Attributes.SetAttribute("aria-label", l["Pagination"].Value);

        IQueryCollection query = ViewContext.HttpContext.Request.Query;
        var html = new StringBuilder();

        if (TotalCount is int total && PageSize > 0)
        {
            int first = (CurrentPage - 1) * PageSize + 1;
            int last = Math.Min(total, CurrentPage * PageSize);
            html.Append($"<span class=\"mat-pager__info\">{HtmlEncoder.Default.Encode(l["{0}–{1} of {2}", first, last, total].Value)}</span>");
        }

        html.Append("<div class=\"mat-pager__pages\">");
        AppendArrow(html, query, CurrentPage - 1, "chevron-left", l["Previous page"].Value, "prev", CurrentPage > 1);

        int previous = 0;
        foreach (int page in PagesToShow())
        {
            if (previous != 0 && page - previous > 1)
            {
                html.Append("<span class=\"mat-pager__gap\" aria-hidden=\"true\">…</span>");
            }

            html.Append(page == CurrentPage
                ? $"<span class=\"mat-pager__btn is-current\" aria-current=\"page\">{page}</span>"
                : $"<a class=\"mat-pager__btn\" href=\"{BuildHref(query, page)}\" aria-label=\"{HtmlEncoder.Default.Encode(l["Page {0}", page].Value)}\">{page}</a>");
            previous = page;
        }

        AppendArrow(html, query, CurrentPage + 1, "chevron-right", l["Next page"].Value, "next", CurrentPage < TotalPages);
        html.Append("</div>");
        output.Content.SetHtmlContent(html.ToString());
    }

    private IEnumerable<int> PagesToShow()
    {
        var pages = new SortedSet<int> { 1, TotalPages };
        for (int page = CurrentPage - 2; page <= CurrentPage + 2; page++)
        {
            if (page >= 1 && page <= TotalPages)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private void AppendArrow(StringBuilder html, IQueryCollection query, int page, string icon, string label, string rel, bool enabled)
    {
        string encodedLabel = HtmlEncoder.Default.Encode(label);
        string svg = toolkit.IconMarkup(icon);
        html.Append(enabled
            ? $"<a class=\"mat-pager__btn\" href=\"{BuildHref(query, page)}\" aria-label=\"{encodedLabel}\" rel=\"{rel}\">{svg}</a>"
            : $"<span class=\"mat-pager__btn is-disabled\" aria-hidden=\"true\">{svg}</span>");
    }

    private string BuildHref(IQueryCollection query, int page)
    {
        var sb = new StringBuilder("?");
        bool first = true;
        foreach (KeyValuePair<string, StringValues> pair in query)
        {
            if (string.Equals(pair.Key, PageParam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in pair.Value)
            {
                if (!first)
                {
                    sb.Append('&');
                }

                sb.Append(UrlEncoder.Default.Encode(pair.Key)).Append('=').Append(UrlEncoder.Default.Encode(value ?? string.Empty));
                first = false;
            }
        }

        if (!first)
        {
            sb.Append('&');
        }

        sb.Append(UrlEncoder.Default.Encode(PageParam)).Append('=').Append(page);
        return HtmlEncoder.Default.Encode(sb.ToString());
    }
}

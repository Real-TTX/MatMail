using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;

namespace MatMail.Controls;

/// <summary>
/// The one table of the application: renders <c>&lt;div class="data-table-wrap"&gt;&lt;table class="data-table"&gt;…</c> around the
/// &lt;thead&gt;/&lt;tbody&gt; given as child content, or a friendly empty state when <c>item-count</c> is 0. Put class "is-actions"
/// on the last &lt;th&gt;/&lt;td&gt; for the right-aligned action icons; rows with <c>data-row-href</c> open on click.
/// Several tables may share a page: nothing in here is id based.
/// Usage: &lt;mm-table item-count="Model.Rows.Count" empty-text="No records."&gt;&lt;thead&gt;…&lt;/mm-table&gt;
/// </summary>
[HtmlTargetElement("mm-table")]
public sealed class TableTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public TableTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("item-count")]
    public int ItemCount { get; set; }

    [HtmlAttributeName("empty-text")]
    public string? EmptyText { get; set; }

    [HtmlAttributeName("empty-icon")]
    public string EmptyIcon { get; set; } = "inbox";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (ItemCount <= 0)
        {
            output.Attributes.SetAttribute("class", "empty-state");
            output.Content.SetHtmlContent(
                $"<svg class=\"icon empty-state__icon\" aria-hidden=\"true\"><use href=\"#i-{HtmlEncoder.Default.Encode(EmptyIcon)}\"/></svg>" +
                $"<p>{HtmlEncoder.Default.Encode(EmptyText ?? _l["No records found."].Value)}</p>");
            return;
        }

        output.Attributes.SetAttribute("class", "data-table-wrap");
        TagHelperContent children = await output.GetChildContentAsync();
        output.Content.SetHtmlContent("<table class=\"data-table\">");
        output.Content.AppendHtml(children);
        output.Content.AppendHtml("</table>");
    }
}

/// <summary>
/// Pagination below a table: "first–last of total", previous/next and a window of page numbers. All other query-string
/// parameters (search, filters, sort) are kept; only the page parameter changes. Renders nothing for a single page unless
/// <c>total-count</c> is given. Several paginations on one page use different <c>page-param</c> names.
/// Usage: &lt;mm-pagination current-page="Model.PageNumber" total-pages="Model.TotalPages" total-count="Model.TotalCount" page-size="20" /&gt;
/// </summary>
[HtmlTargetElement("mm-pagination", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PaginationTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public PaginationTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

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
        output.Attributes.SetAttribute("class", "pagination");
        output.Attributes.SetAttribute("aria-label", _l["Pagination"].Value);

        IQueryCollection query = ViewContext.HttpContext.Request.Query;
        var html = new StringBuilder();

        if (TotalCount is int total && PageSize > 0)
        {
            int first = (CurrentPage - 1) * PageSize + 1;
            int last = Math.Min(total, CurrentPage * PageSize);
            html.Append($"<span class=\"pagination__info\">{HtmlEncoder.Default.Encode(_l["{0}–{1} of {2}", first, last, total].Value)}</span>");
        }

        html.Append("<span class=\"pagination__pages\">");
        AppendArrow(html, query, CurrentPage - 1, "chevron-left", _l["Previous page"].Value, CurrentPage > 1);

        int previous = 0;
        foreach (int page in PagesToShow())
        {
            if (previous != 0 && page - previous > 1)
            {
                html.Append("<span class=\"pagination__gap\" aria-hidden=\"true\">…</span>");
            }

            string active = page == CurrentPage ? " is-active" : string.Empty;
            string current = page == CurrentPage ? " aria-current=\"page\"" : string.Empty;
            html.Append($"<a class=\"pagination__link{active}\" href=\"{BuildHref(query, page)}\"{current}>{page}</a>");
            previous = page;
        }

        AppendArrow(html, query, CurrentPage + 1, "chevron-right", _l["Next page"].Value, CurrentPage < TotalPages);
        html.Append("</span>");
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

    private void AppendArrow(StringBuilder html, IQueryCollection query, int page, string icon, string label, bool enabled)
    {
        string encodedLabel = HtmlEncoder.Default.Encode(label);
        string svg = $"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{icon}\"/></svg>";
        if (enabled)
        {
            html.Append($"<a class=\"pagination__link pagination__arrow\" href=\"{BuildHref(query, page)}\" title=\"{encodedLabel}\" aria-label=\"{encodedLabel}\">{svg}</a>");
        }
        else
        {
            html.Append($"<span class=\"pagination__link pagination__arrow is-disabled\" aria-hidden=\"true\">{svg}</span>");
        }
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

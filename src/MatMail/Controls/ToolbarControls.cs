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

/// <summary>One option of a toolbar select, read from the &lt;option&gt; children of the page.</summary>
internal sealed record ToolbarOption(string Value, string Label);

/// <summary>What the parts of a toolbar tell the toolbar: the search, the filters (selects and fields) and the sort order.</summary>
internal sealed class ToolbarParts
{
    public (string Name, string? Value, string? Placeholder)? Search { get; set; }

    public List<ToolbarFilter> Filters { get; } = new();

    public ToolbarFilter? Sort { get; set; }
}

/// <summary>A filter of the filter dialog (a select, or a field of another type) or the sort order (a select named "Sort").</summary>
internal sealed record ToolbarFilter(string Name, string Label, string? Value, IReadOnlyList<ToolbarOption>? Options, string Type = "select");

/// <summary>
/// The toolbar above every list (LIST-02, LIST-03): the search is a field at the left, <b>Filter</b> and <b>Sort</b> are buttons at the right that open dialogs
/// (DIALOG-02) – selects and dates never stand in the bar itself. A tool in effect shows on its button and as a chip with an X (LIST-04). All fields of all dialogs
/// belong to one GET form, so every dialog sends the complete state and the state lives in the address. The parts are given as children
/// (<c>mm-toolbar-search</c>, <c>mm-toolbar-select</c>, <c>mm-toolbar-field</c>); a select named "Sort" is the sort order, every other part is a filter.
/// Usage: &lt;mm-toolbar count="Model.TotalCount"&gt;&lt;mm-toolbar-search …/&gt;&lt;mm-toolbar-select …&gt;…&lt;/mm-toolbar&gt;
/// </summary>
[HtmlTargetElement("mm-toolbar")]
public sealed class ToolbarTagHelper(IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
    /// <summary>Number of matches shown in the bar. Omit for none.</summary>
    [HtmlAttributeName("count")]
    public int? Count { get; set; }

    /// <summary>Names the list for screen readers ("Mailboxes tools").</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var parts = new ToolbarParts();
        context.Items[typeof(ToolbarParts)] = parts;
        await output.GetChildContentAsync();

        HttpContext http = ViewContext.HttpContext;
        string id = "tb" + (http.Items.TryGetValue("mm-toolbar-seq", out object? seq) ? (int)seq! + 1 : 1);
        http.Items["mm-toolbar-seq"] = int.Parse(id[2..]);
        string form = id + "-form";
        string path = http.Request.PathBase + http.Request.Path;
        HtmlEncoder enc = HtmlEncoder.Default;

        string[] ownNames = parts.Filters.Select(f => f.Name).Concat(parts.Sort is null ? [] : [parts.Sort.Name]).Concat(parts.Search is null ? [] : [parts.Search.Value.Name]).Append("PageNumber").ToArray();
        int activeFilters = parts.Filters.Count(f => !string.IsNullOrEmpty(f.Value));
        bool sortActive = parts.Sort is { Value: { Length: > 0 } sortValue, Options: { Count: > 0 } sortOptions } && sortValue != sortOptions[0].Value;
        string? sortLabel = sortActive ? parts.Sort!.Options!.FirstOrDefault(o => o.Value == parts.Sort.Value)?.Label : null;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mm-toolbar");
        var html = new StringBuilder();

        // ---- the bar
        html.Append($"<div class=\"mat-toolbar\" role=\"toolbar\" aria-label=\"{enc.Encode(Label ?? l["List"].Value)} – {enc.Encode(l["Tools"].Value)}\">");
        if (parts.Search is { } search)
        {
            string name = enc.Encode(search.Name);
            html.Append("<div class=\"mat-input-icon mat-toolbar__search\">").Append(toolkit.IconMarkup("search"));
            html.Append($"<input class=\"mat-input\" type=\"search\" id=\"{id}-q\" name=\"{name}\" form=\"{form}\" value=\"{enc.Encode(search.Value ?? string.Empty)}\" ");
            html.Append($"placeholder=\"{enc.Encode(search.Placeholder ?? l["Search…"].Value)}\" aria-label=\"{enc.Encode(l["Search"].Value)}\" autocomplete=\"off\" enterkeyhint=\"search\" data-shortcut=\"/\" /></div>");
        }

        html.Append("<span class=\"mat-toolbar__spacer\"></span>");
        if (Count is int count)
        {
            html.Append($"<span class=\"mat-toolbar__info\" aria-live=\"polite\">{enc.Encode(count == 1 ? l["{0} match", count].Value : l["{0} matches", count].Value)}</span>");
        }

        if (parts.Filters.Count > 0 || parts.Sort is not null)
        {
            html.Append("<div class=\"mat-toolbar__end\">");
            if (parts.Filters.Count > 0)
            {
                html.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-toolbar__btn{(activeFilters > 0 ? " is-active" : string.Empty)}\" data-dialog-open=\"{id}-filter\" aria-haspopup=\"dialog\">");
                html.Append(toolkit.IconMarkup("sliders")).Append($"<span>{enc.Encode(l["Filter"].Value)}</span>");
                html.Append(activeFilters > 0 ? $"<span class=\"mat-badge\">{activeFilters}</span>" : string.Empty).Append("</button>");
            }

            if (parts.Sort is not null)
            {
                html.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-toolbar__btn{(sortActive ? " is-active" : string.Empty)}\" data-dialog-open=\"{id}-sort\" aria-haspopup=\"dialog\">");
                html.Append(toolkit.IconMarkup("sort")).Append($"<span>{enc.Encode(l["Sort"].Value)}</span>");
                html.Append(sortLabel is null ? string.Empty : $"<small>{enc.Encode(sortLabel)}</small>").Append("</button>");
            }

            html.Append("</div>");
        }

        html.Append("</div>");

        // ---- the tools in effect, each with an X that takes it away
        var chips = new StringBuilder();
        foreach (ToolbarFilter filter in parts.Filters.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            string shown = filter.Options?.FirstOrDefault(o => o.Value == filter.Value)?.Label ?? filter.Value!;
            chips.Append($"<span class=\"mat-chip\">{enc.Encode(filter.Label)}: {enc.Encode(shown)}");
            chips.Append($"<a class=\"mat-chip__x\" href=\"{enc.Encode(UrlWithout(path, ViewContext.HttpContext.Request.Query, filter.Name, "PageNumber"))}\" aria-label=\"{enc.Encode(l["Remove the filter {0}", filter.Label].Value)}\">{toolkit.IconMarkup("x")}</a></span>");
        }

        if (chips.Length > 0)
        {
            html.Append($"<div class=\"mat-chips\" role=\"group\" aria-label=\"{enc.Encode(l["Filters in effect"].Value)}\">").Append(chips).Append("</div>");
        }

        // ---- the one form and its dialogs
        html.Append($"<form id=\"{form}\" method=\"get\" action=\"{enc.Encode(path)}\" hidden>");
        foreach (KeyValuePair<string, StringValues> pair in ViewContext.HttpContext.Request.Query.Where(p => !ownNames.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string? value in pair.Value)
            {
                html.Append($"<input type=\"hidden\" name=\"{enc.Encode(pair.Key)}\" value=\"{enc.Encode(value ?? string.Empty)}\" />");
            }
        }

        html.Append("</form>");

        if (parts.Filters.Count > 0)
        {
            html.Append(Dialog(id, "filter", "sliders", l["Filter"].Value, FilterFields(id, form, parts), form, UrlWithout(path, ViewContext.HttpContext.Request.Query, ownNames.Where(n => parts.Filters.Any(f => f.Name == n)).ToArray()), l["Reset filters"].Value));
        }

        if (parts.Sort is not null)
        {
            html.Append(Dialog(id, "sort", "sort", l["Sort"].Value, SortFields(id, form, parts.Sort), form, UrlWithout(path, ViewContext.HttpContext.Request.Query, parts.Sort.Name, "PageNumber"), l["Default order"].Value));
        }

        output.Content.SetHtmlContent(html.ToString());
    }

    private string FilterFields(string id, string form, ToolbarParts parts)
    {
        var fields = new StringBuilder();
        foreach (ToolbarFilter filter in parts.Filters)
        {
            string name = WebUtility.HtmlEncode(filter.Name);
            string fieldId = $"{id}-f-{name}";
            fields.Append($"<div class=\"mat-field\"><label class=\"mat-label\" for=\"{fieldId}\">{WebUtility.HtmlEncode(filter.Label)}</label>");
            if (filter.Options is { } options)
            {
                fields.Append($"<select class=\"mat-select\" id=\"{fieldId}\" name=\"{name}\" form=\"{form}\">");
                foreach (ToolbarOption option in options)
                {
                    fields.Append($"<option value=\"{WebUtility.HtmlEncode(option.Value)}\"{(option.Value == (filter.Value ?? string.Empty) ? " selected" : string.Empty)}>{WebUtility.HtmlEncode(option.Label)}</option>");
                }

                fields.Append("</select>");
            }
            else
            {
                fields.Append($"<input class=\"mat-input\" type=\"{WebUtility.HtmlEncode(filter.Type)}\" id=\"{fieldId}\" name=\"{name}\" form=\"{form}\" value=\"{WebUtility.HtmlEncode(filter.Value ?? string.Empty)}\" />");
            }

            fields.Append("</div>");
        }

        if (parts.Search is { Value.Length: > 0 } || parts.Sort is not null)
        {
            fields.Append($"<p class=\"mat-hint\">{WebUtility.HtmlEncode(l["The search and the sort order in effect are kept."].Value)}</p>");
        }

        return fields.ToString();
    }

    private string SortFields(string id, string form, ToolbarFilter sort)
    {
        var options = new StringBuilder($"<fieldset class=\"mat-field mat-fieldset\"><legend class=\"mat-label\">{WebUtility.HtmlEncode(l["Sort by"].Value)}</legend><div class=\"mat-options\">");
        string selected = string.IsNullOrEmpty(sort.Value) || sort.Options!.All(o => o.Value != sort.Value) ? sort.Options![0].Value : sort.Value;
        int index = 0;
        foreach (ToolbarOption option in sort.Options!)
        {
            options.Append($"<label class=\"mat-option\"><input type=\"radio\" id=\"{id}-sort-{index++}\" name=\"{WebUtility.HtmlEncode(sort.Name)}\" value=\"{WebUtility.HtmlEncode(option.Value)}\" form=\"{form}\"{(option.Value == selected ? " checked" : string.Empty)} /><span>{WebUtility.HtmlEncode(option.Label)}</span></label>");
        }

        return options.Append("</div></fieldset>").ToString();
    }

    private string Dialog(string id, string kind, string icon, string title, string body, string form, string resetHref, string resetText)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        return $"<dialog class=\"mat-dialog mat-dialog--sm\" id=\"{id}-{kind}\" aria-labelledby=\"{id}-{kind}-title\"><div class=\"mat-dialog__form\">" +
               $"<div class=\"mat-dialog__head\">{toolkit.IconMarkup(icon)}<h2 class=\"mat-dialog__title\" id=\"{id}-{kind}-title\">{enc.Encode(title)}</h2>" +
               $"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close\" data-dialog-close aria-label=\"{enc.Encode(l["Close"].Value)}\">{toolkit.IconMarkup("x")}</button></div>" +
               $"<div class=\"mat-dialog__body mat-form\">{body}</div>" +
               "<div class=\"mat-dialog__foot\"><div class=\"mat-btn-row mat-btn-row--plain\">" +
               $"<button type=\"submit\" form=\"{form}\" class=\"mat-btn mat-btn--primary\">{toolkit.IconMarkup(icon)}<span>{enc.Encode(l["Apply"].Value)}</span></button>" +
               $"<button type=\"button\" class=\"mat-btn mat-btn--secondary\" data-dialog-close>{enc.Encode(l["Cancel"].Value)}</button><span class=\"mat-btn-row__spacer\"></span>" +
               $"<a class=\"mat-btn mat-btn--ghost mat-btn--end\" href=\"{enc.Encode(resetHref)}\">{enc.Encode(resetText)}</a></div></div></div></dialog>";
    }

    /// <summary>The address of the list with every tool in effect except the named ones (the page number goes too: the list is another one).</summary>
    private static string UrlWithout(string path, IQueryCollection query, params string[] names)
    {
        var pairs = new List<string>();
        foreach (KeyValuePair<string, StringValues> pair in query)
        {
            if (names.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Key.Equals("PageNumber", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in pair.Value)
            {
                pairs.Add(UrlEncoder.Default.Encode(pair.Key) + "=" + UrlEncoder.Default.Encode(value ?? string.Empty));
            }
        }

        return pairs.Count == 0 ? path : path + "?" + string.Join('&', pairs);
    }
}

/// <summary>The search of a toolbar. Usage: &lt;mm-toolbar-search name="Search" value="@Model.Search" placeholder="…" /&gt;</summary>
[HtmlTargetElement("mm-toolbar-search", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarSearchTagHelper : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = "Search";

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ToolbarParts), out object? parts))
        {
            ((ToolbarParts)parts).Search = (Name, Value, Placeholder);
        }

        output.SuppressOutput();
    }
}

/// <summary>
/// A filter of a toolbar, or its sort order when it is named "Sort". Provide &lt;option value="…"&gt; children (the first one is "All" / the default),
/// the one matching <c>value</c> is selected. Usage: &lt;mm-toolbar-select name="Type" label="Type" value="@Model.Type"&gt;…&lt;/mm-toolbar-select&gt;
/// </summary>
[HtmlTargetElement("mm-toolbar-select")]
public sealed class ToolbarSelectTagHelper : TagHelper
{
    // <option value="x">Label</option>: group 2 is the value, group 3 the label
    private static readonly Regex OptionPattern = new(@"<option\b([^>]*?)\bvalue\s*=\s*""([^""]*)""[^>]*>(.*?)</option>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string children = (await output.GetChildContentAsync()).GetContent();
        var options = OptionPattern.Matches(children)
            .Select(m => new ToolbarOption(WebUtility.HtmlDecode(m.Groups[2].Value), WebUtility.HtmlDecode(TagPattern.Replace(m.Groups[3].Value, string.Empty)).Trim()))
            .ToList();
        if (context.Items.TryGetValue(typeof(ToolbarParts), out object? found))
        {
            var parts = (ToolbarParts)found;
            var filter = new ToolbarFilter(Name, Label, Value, options);
            if (Name.Equals("Sort", StringComparison.OrdinalIgnoreCase))
            {
                parts.Sort = filter;
            }
            else
            {
                parts.Filters.Add(filter);
            }
        }

        output.SuppressOutput();
    }
}

/// <summary>A filter of a toolbar that is a field of its own (a date, a number …). Usage: &lt;mm-toolbar-field name="From" label="From" type="date" value="…" /&gt;</summary>
[HtmlTargetElement("mm-toolbar-field", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarFieldTagHelper : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("type")]
    public string Type { get; set; } = "text";

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ToolbarParts), out object? parts))
        {
            ((ToolbarParts)parts).Filters.Add(new ToolbarFilter(Name, Label, Value, null, Type));
        }

        output.SuppressOutput();
    }
}

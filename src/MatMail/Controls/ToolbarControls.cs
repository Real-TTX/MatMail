using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// The toolbar above every list: search text, filters and sort order, always in this place. It is a GET form, so the state lives
/// in the URL; filters apply as soon as they change (search on Enter). On narrow screens everything but the search field folds
/// into a "Filter" panel (app.js). Drop the toolbar parts inside it.
/// Usage: &lt;mm-toolbar count="Model.TotalCount"&gt;&lt;mm-toolbar-search …/&gt;&lt;mm-toolbar-select …&gt;…&lt;/mm-toolbar&gt;
/// </summary>
[HtmlTargetElement("mm-toolbar")]
public sealed class ToolbarTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public ToolbarTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("method")]
    public string Method { get; set; } = "get";

    /// <summary>Number of matches shown at the end of the toolbar. Omit for none.</summary>
    [HtmlAttributeName("count")]
    public int? Count { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "form";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar");
        output.Attributes.SetAttribute("method", Method);
        output.Attributes.SetAttribute("role", "search");
        output.Attributes.SetAttribute("data-t-filters", _l["Filter"].Value);
        output.Attributes.SetAttribute("data-t-reset", _l["Reset filters"].Value);
        output.Attributes.SetAttribute("data-t-apply", _l["Apply"].Value);
        output.Attributes.SetAttribute("data-t-close", _l["Close"].Value);

        TagHelperContent children = await output.GetChildContentAsync();
        output.Content.SetHtmlContent(children);

        if (Count is int count)
        {
            string text = HtmlEncoder.Default.Encode(_l["{0} matches", count].Value);
            output.Content.AppendHtml($"<div class=\"toolbar__group toolbar__count\">{text}</div>");
        }

        string apply = HtmlEncoder.Default.Encode(_l["Apply"].Value);
        output.Content.AppendHtml($"<noscript><div class=\"toolbar__group\"><button class=\"btn btn--secondary\" type=\"submit\">{apply}</button></div></noscript>");
    }
}

/// <summary>Search box of a toolbar. Usage: &lt;mm-toolbar-search name="Search" value="@Model.Search" placeholder="…" /&gt;</summary>
[HtmlTargetElement("mm-toolbar-search", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarSearchTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public ToolbarSearchTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("name")]
    public string Name { get; set; } = "Search";

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        string id = enc.Encode(Name);
        string label = enc.Encode(_l["Search"].Value);
        string placeholder = enc.Encode(Placeholder ?? _l["Search…"].Value);

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__search");
        output.Content.SetHtmlContent(
            $"<label class=\"sr-only\" for=\"{id}\">{label}</label>" +
            "<svg class=\"icon toolbar__search-icon\" aria-hidden=\"true\"><use href=\"#i-search\"/></svg>" +
            $"<input class=\"form-control\" type=\"search\" id=\"{id}\" name=\"{id}\" value=\"{enc.Encode(Value ?? string.Empty)}\" placeholder=\"{placeholder}\" autocomplete=\"off\" />");
    }
}

/// <summary>
/// A labelled filter or sort select of a toolbar; changing it applies the toolbar. Provide &lt;option value="…"&gt; children,
/// the one matching <c>value</c> is selected. Usage: &lt;mm-toolbar-select name="Sort" label="Sort" value="@Model.Sort"&gt;…&lt;/mm-toolbar-select&gt;
/// </summary>
[HtmlTargetElement("mm-toolbar-select")]
public sealed class ToolbarSelectTagHelper : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        string id = enc.Encode(Name);
        string options = HtmlOptionHelper.MarkSelected((await output.GetChildContentAsync()).GetContent(), Value);

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__filter");
        output.Content.SetHtmlContent(
            $"<label for=\"{id}\">{enc.Encode(Label)}</label>" +
            $"<select class=\"form-control\" id=\"{id}\" name=\"{id}\" data-autosubmit>{options}</select>");
    }
}

/// <summary>A labelled input of a toolbar (a date, a number …); applies when it changes. Usage: &lt;mm-toolbar-field name="From" label="From" type="date" value="…" /&gt;</summary>
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
        HtmlEncoder enc = HtmlEncoder.Default;
        string id = enc.Encode(Name);
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__filter");
        output.Content.SetHtmlContent(
            $"<label for=\"{id}\">{enc.Encode(Label)}</label>" +
            $"<input class=\"form-control\" type=\"{enc.Encode(Type)}\" id=\"{id}\" name=\"{id}\" value=\"{enc.Encode(Value ?? string.Empty)}\" data-autosubmit />");
    }
}

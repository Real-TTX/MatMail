using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

/// <summary>
/// A tab bar of links (each tab is its own page, e.g. the account pages). Usage:
/// &lt;mm-tabbar&gt;&lt;mm-tab title="Profile" href="/Account" active="true" icon="user" /&gt;…&lt;/mm-tabbar&gt;
/// </summary>
[HtmlTargetElement("mm-tabbar")]
public sealed class TabBarTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "tabbar");
    }
}

/// <summary>One link tab of an <c>mm-tabbar</c>.</summary>
[HtmlTargetElement("mm-tab")]
public sealed class TabTagHelper : TagHelper
{
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    [HtmlAttributeName("href")]
    public string Href { get; set; } = "#";

    [HtmlAttributeName("active")]
    public bool Active { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "a";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Active ? "tabbar__tab is-active" : "tabbar__tab");
        output.Attributes.SetAttribute("href", Href);
        if (Active)
        {
            output.Attributes.SetAttribute("aria-current", "page");
        }

        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.SetHtmlContent($"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{HtmlEncoder.Default.Encode(Icon)}\"/></svg>");
        }

        if (!string.IsNullOrEmpty(Title))
        {
            output.Content.Append(Title);
        }
        else
        {
            output.Content.AppendHtml(await output.GetChildContentAsync());
        }
    }
}

/// <summary>
/// Tabs that switch panels on the same page (one big form, split into sections). The tab bar is built from the child
/// <c>mm-tab-panel</c>s. Several can share a page: ids come from a per-request counter and the script only looks inside its own
/// <c>[data-tabs]</c> element. A tab that contains a validation error opens on its own.
/// Usage: &lt;mm-tabs&gt;&lt;mm-tab-panel title="General" icon="user"&gt;…&lt;/mm-tab-panel&gt;…&lt;/mm-tabs&gt;
/// </summary>
[HtmlTargetElement("mm-tabs")]
public sealed class TabsTagHelper : TagHelper
{
    internal const string ItemsKey = "mm-tabs-panels";
    internal const string ActiveKey = "mm-tabs-active";

    /// <summary>Key of the panel that is open first (default: the first one).</summary>
    [HtmlAttributeName("active")]
    public string? Active { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var panels = new List<TabPanelInfo>();
        context.Items[ItemsKey] = panels;
        context.Items[ActiveKey] = Active;

        TagHelperContent content = await output.GetChildContentAsync();

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "tabs");
        output.Attributes.SetAttribute("data-tabs", string.Empty);

        HtmlEncoder enc = HtmlEncoder.Default;
        var nav = new StringBuilder("<div class=\"tabbar tabbar--panels\" role=\"tablist\">");
        foreach (TabPanelInfo panel in panels)
        {
            string selected = panel.IsActive ? " is-active" : string.Empty;
            string icon = string.IsNullOrEmpty(panel.Icon)
                ? string.Empty
                : $"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{enc.Encode(panel.Icon)}\"/></svg>";
            nav.Append($"<button type=\"button\" class=\"tabbar__tab{selected}\" role=\"tab\" id=\"{panel.Id}-tab\" data-tab=\"{panel.Id}\" aria-controls=\"{panel.Id}\" aria-selected=\"{(panel.IsActive ? "true" : "false")}\">{icon}<span>{enc.Encode(panel.Title)}</span></button>");
        }

        nav.Append("</div>");
        output.Content.SetHtmlContent(nav.ToString());
        output.Content.AppendHtml(content);
    }
}

internal sealed record TabPanelInfo(string Id, string Title, string? Icon, bool IsActive);

/// <summary>One panel of an <c>mm-tabs</c>.</summary>
[HtmlTargetElement("mm-tab-panel", ParentTag = "mm-tabs")]
public sealed class TabPanelTagHelper : TagHelper
{
    [HtmlAttributeName("title")]
    public string Title { get; set; } = string.Empty;

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    /// <summary>Optional key to open this panel through <c>mm-tabs active="…"</c>.</summary>
    [HtmlAttributeName("key")]
    public string? Key { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var panels = (List<TabPanelInfo>)context.Items[TabsTagHelper.ItemsKey];
        string? wanted = context.Items[TabsTagHelper.ActiveKey] as string;

        int sequence = ViewContext.HttpContext.Items.TryGetValue("mm-tab-seq", out object? current) ? (int)current! + 1 : 1;
        ViewContext.HttpContext.Items["mm-tab-seq"] = sequence;
        string id = $"tabpanel-{sequence}";

        // Without a wanted key the first panel opens; an unknown key opens none and app.js falls back to the first one.
        bool isActive = string.IsNullOrEmpty(wanted) ? panels.Count == 0 : string.Equals(wanted, Key, StringComparison.Ordinal);
        panels.Add(new TabPanelInfo(id, Title, Icon, isActive));

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "tab-panel");
        output.Attributes.SetAttribute("id", id);
        output.Attributes.SetAttribute("role", "tabpanel");
        output.Attributes.SetAttribute("aria-labelledby", id + "-tab");
        if (!isActive)
        {
            output.Attributes.SetAttribute("hidden", "hidden");
        }

        output.Content.SetHtmlContent(await output.GetChildContentAsync());
    }
}

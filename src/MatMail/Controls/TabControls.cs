using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// Tabs that are links: each tab is a page of its own, the current one carries <c>aria-current</c>. Usage:
/// &lt;mm-tabbar&gt;&lt;mm-tab title="Profile" href="/Account" active="true" icon="user" /&gt;…&lt;/mm-tabbar&gt;
/// </summary>
[HtmlTargetElement("mm-tabbar")]
public sealed class TabBarTagHelper(IStringLocalizer<SharedResource> l) : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-tabs");
        output.Attributes.SetAttribute("aria-label", l["Sections"].Value);
    }
}

/// <summary>One link tab of an <c>mm-tabbar</c>.</summary>
[HtmlTargetElement("mm-tab")]
public sealed class TabTagHelper(Toolkit toolkit) : TagHelper
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
        output.Attributes.SetAttribute("class", Active ? "mat-tab is-active" : "mat-tab");
        output.Attributes.SetAttribute("href", Href);
        if (Active)
        {
            output.Attributes.SetAttribute("aria-current", "page");
        }

        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.SetHtmlContent(toolkit.IconMarkup(Icon));
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
/// <c>mm-tab-panel</c>s; the toolkit's script moves between them (arrow keys, Home and End). Several can share a page: ids come from a per-request counter.
/// A tab that contains a field with an error opens on its own (matmail.js).
/// Usage: &lt;mm-tabs&gt;&lt;mm-tab-panel title="General" icon="user"&gt;…&lt;/mm-tab-panel&gt;…&lt;/mm-tabs&gt;
/// </summary>
[HtmlTargetElement("mm-tabs")]
public sealed class TabsTagHelper(IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
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
        output.Attributes.SetAttribute("data-tabs", string.Empty);

        HtmlEncoder enc = HtmlEncoder.Default;
        var nav = new StringBuilder($"<div class=\"mat-tabs\" role=\"tablist\" aria-label=\"{enc.Encode(l["Sections"].Value)}\">");
        foreach (TabPanelInfo panel in panels)
        {
            string icon = string.IsNullOrEmpty(panel.Icon) ? string.Empty : toolkit.IconMarkup(panel.Icon);
            nav.Append($"<button type=\"button\" class=\"mat-tab\" role=\"tab\" id=\"{panel.Id}-tab\" aria-controls=\"{panel.Id}\" aria-selected=\"{(panel.IsActive ? "true" : "false")}\"{(panel.IsActive ? string.Empty : " tabindex=\"-1\"")}>{icon}<span>{enc.Encode(panel.Title)}</span></button>");
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

        // Without a wanted key the first panel opens; an unknown key opens none and the script falls back to the first one.
        bool isActive = string.IsNullOrEmpty(wanted) ? panels.Count == 0 : string.Equals(wanted, Key, StringComparison.Ordinal);
        panels.Add(new TabPanelInfo(id, Title, Icon, isActive));

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-tabpanel");
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

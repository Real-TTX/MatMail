using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

/// <summary>
/// The row of form actions, always in one line and always in this order: positive first (Save), then neutral (Back), then — pushed
/// to the far side by an <c>mm-button-spacer</c> — destructive (Delete). On narrow screens the row wraps and the spacer vanishes.
/// </summary>
[HtmlTargetElement("mm-button-row")]
public sealed class ButtonRowTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "btn-row");
    }
}

/// <summary>Separates the destructive actions from the others inside an <c>mm-button-row</c>.</summary>
[HtmlTargetElement("mm-button-spacer", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ButtonSpacerTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "btn-row__spacer");
    }
}

/// <summary>
/// The row of actions that belong to a list (New …, Import …), placed directly below the table and aligned left. Delete-style
/// actions are separated from the rest. Pagination is the only thing that may sit between the table and this row.
/// </summary>
[HtmlTargetElement("mm-list-actions")]
public sealed class ListActionsTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "list-actions");
    }
}

/// <summary>
/// A button or a link that looks like one. <c>variant</c>: primary | secondary | danger | ghost. <c>href</c> renders an anchor,
/// otherwise a button; <c>handler</c> posts to a Razor page handler; <c>confirm</c> asks first (dialog in app.js);
/// <c>icon</c> puts an icon in front of the text.
/// Usage: &lt;mm-button variant="primary" icon="save" type="submit"&gt;Save&lt;/mm-button&gt;
/// </summary>
[HtmlTargetElement("mm-button")]
public sealed class ButtonTagHelper : TagHelper
{
    [HtmlAttributeName("variant")]
    public string Variant { get; set; } = "primary";

    [HtmlAttributeName("href")]
    public string? Href { get; set; }

    [HtmlAttributeName("type")]
    public string Type { get; set; } = "submit";

    [HtmlAttributeName("handler")]
    public string? Handler { get; set; }

    [HtmlAttributeName("confirm")]
    public string? Confirm { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string css = $"btn btn--{Variant}";
        TagHelperContent body = await output.GetChildContentAsync();
        output.TagMode = TagMode.StartTagAndEndTag;

        if (Href is not null)
        {
            output.TagName = "a";
            output.Attributes.SetAttribute("class", css);
            output.Attributes.SetAttribute("href", Href);
        }
        else
        {
            output.TagName = "button";
            output.Attributes.SetAttribute("class", css);
            output.Attributes.SetAttribute("type", Type);
            if (!string.IsNullOrEmpty(Handler))
            {
                output.Attributes.SetAttribute("formaction", $"?handler={UrlEncoder.Default.Encode(Handler)}");
                output.Attributes.SetAttribute("formnovalidate", "formnovalidate");
            }
        }

        if (!string.IsNullOrEmpty(Confirm))
        {
            output.Attributes.SetAttribute("data-confirm", Confirm);
        }

        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.AppendHtml($"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{HtmlEncoder.Default.Encode(Icon)}\"/></svg>");
        }

        output.Content.AppendHtml("<span>");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</span>");
    }
}

/// <summary>A small status label. <c>kind</c>: ok | warn | danger | info | muted.</summary>
[HtmlTargetElement("mm-badge")]
public sealed class BadgeTagHelper : TagHelper
{
    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "muted";

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"badge badge--{Kind}");
        TagHelperContent body = await output.GetChildContentAsync();
        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.AppendHtml($"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{HtmlEncoder.Default.Encode(Icon)}\"/></svg>");
        }

        output.Content.AppendHtml(body);
    }
}

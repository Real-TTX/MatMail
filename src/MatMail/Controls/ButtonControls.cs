using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

/// <summary>
/// The row of form actions under the content (FORM-01, FORM-02): the toolkit puts the buttons in the order positive, neutral, the space, destructive,
/// whatever order they are written in. On narrow screens the row wraps. Put an <c>mm-button-spacer</c> before the destructive button.
/// </summary>
[HtmlTargetElement("mm-button-row")]
public sealed class ButtonRowTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        string extra = output.Attributes.TryGetAttribute("class", out TagHelperAttribute? existing) ? " " + existing.Value : string.Empty;
        output.Attributes.SetAttribute("class", "mat-btn-row" + extra);
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
        output.Attributes.SetAttribute("class", "mat-btn-row__spacer");
    }
}

/// <summary>
/// The row of actions that belong to a list (New …, Import …), below the table and the pagination, aligned left (LIST-07); the toolkit keeps the order
/// toolbar, table, pagination, actions whatever the source order is. Put the list in an <c>mm-list</c>.
/// </summary>
[HtmlTargetElement("mm-list-actions")]
public sealed class ListActionsTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-actions");
    }
}

/// <summary>
/// A button or a link that looks like one. <c>variant</c>: primary | secondary | danger | ghost. <c>href</c> renders an anchor,
/// otherwise a button; <c>handler</c> posts to a Razor page handler; <c>confirm</c> asks first, in a dialog of the toolkit (a destructive
/// button asks with Cancel first and the red button last; <c>confirm-ok</c> names the confirming button); <c>icon</c> puts an icon in front of the text;
/// <c>icon-only</c> makes a button of the icon alone (the text becomes its name and its tooltip, BUTTON-04).
/// Usage: &lt;mm-button variant="primary" icon="save" type="submit"&gt;Save&lt;/mm-button&gt;
/// </summary>
[HtmlTargetElement("mm-button")]
public sealed class ButtonTagHelper(Toolkit toolkit) : TagHelper
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

    [HtmlAttributeName("confirm-ok")]
    public string? ConfirmOk { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    [HtmlAttributeName("icon-only")]
    public bool IconOnly { get; set; }

    [HtmlAttributeName("small")]
    public bool Small { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string css = $"mat-btn mat-btn--{Variant}" + (IconOnly ? " mat-btn--icon" : string.Empty) + (Small ? " mat-btn--sm" : string.Empty);
        if (output.Attributes.TryGetAttribute("class", out TagHelperAttribute? extra))
        {
            css += " " + extra.Value;
        }

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
            if (!string.IsNullOrEmpty(ConfirmOk))
            {
                output.Attributes.SetAttribute("data-confirm-ok", ConfirmOk);
            }
        }

        if (IconOnly)
        {
            string name = body.GetContent().Trim();
            output.Attributes.SetAttribute("aria-label", name);
            output.Attributes.SetAttribute("data-tip", name);
            output.Content.SetHtmlContent(toolkit.IconMarkup(Icon ?? "help-circle"));
            return;
        }

        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.AppendHtml(toolkit.IconMarkup(Icon));
        }

        output.Content.AppendHtml("<span>");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</span>");
    }
}

/// <summary>
/// A small label. A status is a pill with a dot and a word (TABLE-03, COLOR-04): <c>kind</c> ok | warn | danger | info | muted.
/// With an <c>icon</c> it is a category (a tag), not a state.
/// </summary>
[HtmlTargetElement("mm-badge")]
public sealed class BadgeTagHelper(Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "muted";

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        TagHelperContent body = await output.GetChildContentAsync();

        if (!string.IsNullOrEmpty(Icon))
        {
            output.Attributes.SetAttribute("class", "mat-tag");
            output.Content.AppendHtml(toolkit.IconMarkup(Icon));
            output.Content.AppendHtml(body);
            return;
        }

        string modifier = Kind switch { "ok" => " mat-pill--ok", "warn" => " mat-pill--warn", "danger" => " mat-pill--error", "info" => " mat-pill--info", _ => string.Empty };
        output.Attributes.SetAttribute("class", "mat-pill" + modifier);
        output.Content.AppendHtml(body);
    }
}

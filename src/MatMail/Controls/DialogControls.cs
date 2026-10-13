using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// A dialog of the toolkit (native &lt;dialog&gt;): a head with the title and the X, a body that scrolls on its own and, with <c>mm-dialog-foot</c>, a foot with the
/// buttons (DIALOG-04). Open it with any element that has <c>data-dialog-open="id"</c>; every <c>data-dialog-close</c> inside closes it. On a phone it fills the
/// screen with the X at the top right (DIALOG-03). Usage: &lt;mm-dialog id="rename" title="Rename" size="sm"&gt;…&lt;mm-dialog-foot&gt;…&lt;/mm-dialog-foot&gt;&lt;/mm-dialog&gt;
/// </summary>
[HtmlTargetElement("mm-dialog")]
public sealed class DialogTagHelper(IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
    internal const string FootKey = "mm-dialog-foot";

    [HtmlAttributeName("id")]
    public string Id { get; set; } = string.Empty;

    [HtmlAttributeName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>sm | md | lg.</summary>
    [HtmlAttributeName("size")]
    public string Size { get; set; } = "md";

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        var foot = new FootHolder();
        context.Items[FootKey] = foot;
        TagHelperContent body = await output.GetChildContentAsync();

        output.TagName = "dialog";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Size == "md" ? "mat-dialog" : $"mat-dialog mat-dialog--{Size}");
        output.Attributes.SetAttribute("id", Id);
        output.Attributes.SetAttribute("aria-labelledby", Id + "-title");

        output.Content.SetHtmlContent(
            "<div class=\"mat-dialog__form\"><div class=\"mat-dialog__head\">" +
            (string.IsNullOrEmpty(Icon) ? string.Empty : toolkit.IconMarkup(Icon)) +
            $"<h2 class=\"mat-dialog__title\" id=\"{enc.Encode(Id)}-title\">{enc.Encode(Title)}</h2>" +
            $"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close\" data-dialog-close aria-label=\"{enc.Encode(l["Close"].Value)}\">{toolkit.IconMarkup("x")}</button></div>" +
            "<div class=\"mat-dialog__body\">");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div>");
        if (foot.Content is not null)
        {
            output.Content.AppendHtml("<div class=\"mat-dialog__foot\"><div class=\"mat-btn-row mat-btn-row--plain\">");
            output.Content.AppendHtml(foot.Content);
            output.Content.AppendHtml("</div></div>");
        }

        output.Content.AppendHtml("</div>");
    }

    internal sealed class FootHolder
    {
        public TagHelperContent? Content { get; set; }
    }
}

/// <summary>The buttons of the foot of an <c>mm-dialog</c>, from positive to negative.</summary>
[HtmlTargetElement("mm-dialog-foot", ParentTag = "mm-dialog")]
public sealed class DialogFootTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(DialogTagHelper.FootKey, out object? holder))
        {
            ((DialogTagHelper.FootHolder)holder).Content = await output.GetChildContentAsync();
        }

        output.SuppressOutput();
    }
}

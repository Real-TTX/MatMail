using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// A modal dialog (native &lt;dialog&gt;). Open it with any element that has <c>data-dialog-open="id"</c>; every
/// <c>data-dialog-close</c> inside closes it. On phones it fills the whole screen. Usage:
/// &lt;mm-dialog id="rename" title="Rename" size="sm"&gt;…&lt;/mm-dialog&gt;
/// </summary>
[HtmlTargetElement("mm-dialog")]
public sealed class DialogTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public DialogTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("id")]
    public string Id { get; set; } = string.Empty;

    [HtmlAttributeName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>sm | md | lg.</summary>
    [HtmlAttributeName("size")]
    public string Size { get; set; } = "md";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        output.TagName = "dialog";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"dialog dialog--{Size}");
        output.Attributes.SetAttribute("id", Id);
        output.Attributes.SetAttribute("aria-labelledby", Id + "-title");

        TagHelperContent body = await output.GetChildContentAsync();
        output.Content.SetHtmlContent(
            "<div class=\"dialog__header\">" +
            $"<h2 class=\"dialog__title\" id=\"{enc.Encode(Id)}-title\">{enc.Encode(Title)}</h2>" +
            $"<button type=\"button\" class=\"icon-btn\" data-dialog-close aria-label=\"{enc.Encode(_l["Close"].Value)}\">" +
            "<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-x\"/></svg></button></div>" +
            "<div class=\"dialog__body\">");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div>");
    }
}

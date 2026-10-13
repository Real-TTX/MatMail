using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

/// <summary>
/// An icon from the page's inline sprites (<see cref="Toolkit"/>). Usage: <c>&lt;mm-icon name="mail" /&gt;</c>; a class on the tag goes to the icon.
/// The name has to be in the sprite of the toolkit or in <c>matmail-icons.svg</c> (a unit test checks the pages and the scripts).
/// </summary>
[HtmlTargetElement("mm-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper(Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;

        string cssClass = "mat-icon";
        if (output.Attributes.TryGetAttribute("class", out TagHelperAttribute? extra))
        {
            cssClass += " " + extra.Value;
        }

        output.Attributes.SetAttribute("class", cssClass);
        output.Attributes.SetAttribute("aria-hidden", "true");
        output.Attributes.SetAttribute("focusable", "false");
        output.Content.SetHtmlContent($"<use href=\"{HtmlEncoder.Default.Encode(toolkit.Symbol(Name))}\"></use>");
    }
}

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

internal static class ControlClasses
{
    /// <summary>Sets the class of the element and keeps what the author wrote in <c>class</c> after it.</summary>
    public static void Merge(TagHelperOutput output, string toolkitClasses)
    {
        string extra = output.Attributes.TryGetAttribute("class", out TagHelperAttribute? existing) ? " " + existing.Value : string.Empty;
        output.Attributes.SetAttribute("class", toolkitClasses + extra);
    }
}

/// <summary>
/// A message that stays on the page (an alert of the toolkit): <c>kind</c> info | warn | danger | ok, an icon that fits the kind (or <c>icon</c>), an optional
/// bold <c>title</c> and the text as the content. A message that only says "done" is a toast (<c>Notify</c>), not this.
/// Usage: &lt;mm-alert kind="warn"&gt;This server uses a self-signed certificate.&lt;/mm-alert&gt;
/// </summary>
[HtmlTargetElement("mm-alert")]
public sealed class AlertTagHelper(Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "info";

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        (string tone, string icon, string role) = Kind switch
        {
            "warn" or "warning" => ("warning", "alert", "status"),
            "danger" or "error" => ("danger", "x-circle", "alert"),
            "ok" or "success" => ("success", "check-circle", "status"),
            _ => ("info", "info", "status"),
        };

        TagHelperContent body = await output.GetChildContentAsync();
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        ControlClasses.Merge(output, "mat-alert mat-alert--" + tone);
        output.Attributes.SetAttribute("role", role);
        output.Content.SetHtmlContent(toolkit.IconMarkup(string.IsNullOrEmpty(Icon) ? icon : Icon));
        output.Content.AppendHtml("<div class=\"mat-alert__body\">");
        if (!string.IsNullOrEmpty(Title))
        {
            output.Content.AppendHtml($"<span class=\"mat-alert__title\">{HtmlEncoder.Default.Encode(Title)}</span>");
        }

        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div>");
    }
}

/// <summary>
/// A card of the toolkit: a head with an icon and a title, the content in the body (the blocks in it stand under each other). <c>flush</c> takes the padding
/// away from the body, for a table that fills the card; <c>accent</c> marks the one card to look at first.
/// Usage: &lt;mm-card title="Server" icon="server"&gt;…&lt;/mm-card&gt;
/// </summary>
[HtmlTargetElement("mm-card")]
public sealed class CardTagHelper(Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    [HtmlAttributeName("flush")]
    public bool Flush { get; set; }

    [HtmlAttributeName("accent")]
    public bool Accent { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        TagHelperContent body = await output.GetChildContentAsync();
        output.TagName = "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        ControlClasses.Merge(output, "mat-card" + (Flush ? " mat-card--flush" : string.Empty) + (Accent ? " mat-card--accent" : string.Empty));

        output.Content.SetHtmlContent(string.Empty);
        if (!string.IsNullOrEmpty(Title))
        {
            string icon = string.IsNullOrEmpty(Icon) ? string.Empty : toolkit.IconMarkup(Icon);
            output.Content.AppendHtml($"<div class=\"mat-card__head\">{icon}<h2 class=\"mat-card__title\">{HtmlEncoder.Default.Encode(Title)}</h2></div>");
        }

        output.Content.AppendHtml("<div class=\"mat-card__body mat-stack\">");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div>");
    }
}

/// <summary>
/// A bar for a share of something (the storage of a mailbox). <c>percent</c> is 0 to 100; <c>state</c> is what <c>Fmt.UsageState</c> says ("" fine, "warn"
/// getting full, "high" and "full" a problem) and gives the bar its tone; <c>label</c> names it for screen readers. The width is set by the toolkit from
/// <c>data-value</c>, so the markup has no style attribute (CODE-02).
/// Usage: &lt;mm-meter percent="63" state="" label="Storage used" small="true" /&gt;
/// </summary>
[HtmlTargetElement("mm-meter", TagStructure = TagStructure.WithoutEndTag)]
public sealed class MeterTagHelper : TagHelper
{
    [HtmlAttributeName("percent")]
    public int Percent { get; set; }

    [HtmlAttributeName("state")]
    public string? State { get; set; }

    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    [HtmlAttributeName("small")]
    public bool Small { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        int percent = Math.Clamp(Percent, 0, 100);
        string? tone = State switch { "warn" => "warn", "high" or "full" => "error", _ => null };
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        ControlClasses.Merge(output, "mat-meter" + (Small ? " mat-meter--sm" : string.Empty));
        output.Attributes.SetAttribute("role", "progressbar");
        output.Attributes.SetAttribute("aria-valuemin", "0");
        output.Attributes.SetAttribute("aria-valuemax", "100");
        output.Attributes.SetAttribute("aria-valuenow", percent);
        if (!string.IsNullOrEmpty(Label))
        {
            output.Attributes.SetAttribute("aria-label", Label);
        }

        output.Content.SetHtmlContent($"<div class=\"mat-meter__fill\" data-value=\"{percent}\"{(tone is null ? string.Empty : $" data-tone=\"{tone}\"")}></div>");
    }
}

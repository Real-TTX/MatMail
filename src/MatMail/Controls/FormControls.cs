using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// The one form of the application: a POST form with the anti-forgery token and a summary of the model-level errors on top.
/// Fields (<c>mm-field</c>, <c>mm-picker</c>), sections (<c>mm-form-section</c>) and the button row go inside.
/// Usage: &lt;mm-form&gt;…&lt;/mm-form&gt;
/// </summary>
[HtmlTargetElement("mm-form")]
public sealed class FormTagHelper : TagHelper
{
    private readonly IHtmlGenerator _generator;

    public FormTagHelper(IHtmlGenerator generator) => _generator = generator;

    [HtmlAttributeName("method")]
    public string Method { get; set; } = "post";

    [HtmlAttributeName("action")]
    public string? Action { get; set; }

    /// <summary>Use "multipart/form-data" for file uploads.</summary>
    [HtmlAttributeName("enctype")]
    public string? Enctype { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    /// <summary>
    /// The address of this page without the handler of the request that rendered it: a page that came back from "Test connection"
    /// (?handler=Test) must still save with "Save" instead of repeating the test.
    /// </summary>
    private string CurrentPage()
    {
        HttpRequest request = ViewContext.HttpContext.Request;
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> query =
            request.Query.Where(pair => !string.Equals(pair.Key, "handler", StringComparison.OrdinalIgnoreCase));
        return QueryHelpers.AddQueryString(request.PathBase + request.Path, query);
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "form";
        output.TagMode = TagMode.StartTagAndEndTag;
        string extra = output.Attributes.TryGetAttribute("class", out TagHelperAttribute? existing) ? " " + existing.Value : string.Empty;
        output.Attributes.SetAttribute("class", "form" + extra);
        output.Attributes.SetAttribute("method", Method);
        output.Attributes.SetAttribute("action", string.IsNullOrEmpty(Action) ? CurrentPage() : Action);

        if (!string.IsNullOrEmpty(Enctype))
        {
            output.Attributes.SetAttribute("enctype", Enctype);
        }

        TagHelperContent children = await output.GetChildContentAsync();
        TagBuilder summary = _generator.GenerateValidationSummary(
            ViewContext, excludePropertyErrors: true, message: null, headerTag: null, htmlAttributes: new { @class = "form-summary" });
        output.Content.SetHtmlContent(summary);
        output.Content.AppendHtml(children);

        if (string.Equals(Method, "post", StringComparison.OrdinalIgnoreCase))
        {
            output.PostContent.AppendHtml(_generator.GenerateAntiforgery(ViewContext));
        }
    }
}

/// <summary>
/// A titled group of fields inside a form (a "card"). Supports dependent visibility like <c>mm-field</c>.
/// Usage: &lt;mm-form-section title="Receiving" description="…" icon="inbox"&gt;…&lt;/mm-form-section&gt;
/// </summary>
[HtmlTargetElement("mm-form-section")]
public sealed class FormSectionTagHelper : TagHelper
{
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    [HtmlAttributeName("description")]
    public string? Description { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        output.TagName = "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "form-section");
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-when-field", ShowWhenField);
            output.Attributes.SetAttribute("data-show-when-value", ShowWhenValue ?? string.Empty);
        }

        TagHelperContent body = await output.GetChildContentAsync();
        if (!string.IsNullOrEmpty(Title))
        {
            string icon = string.IsNullOrEmpty(Icon) ? string.Empty : $"<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-{enc.Encode(Icon)}\"/></svg>";
            output.Content.AppendHtml($"<header class=\"form-section__header\"><h3 class=\"form-section__title\">{icon}{enc.Encode(Title)}</h3>");
            if (!string.IsNullOrEmpty(Description))
            {
                output.Content.AppendHtml($"<p class=\"form-section__description\">{enc.Encode(Description)}</p>");
            }

            output.Content.AppendHtml("</header>");
        }

        output.Content.AppendHtml("<div class=\"form-section__body\">");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div>");
    }
}

/// <summary>
/// A complete field built from an <c>asp-for</c> expression: label, input, validation message and help text. Names and ids derive
/// from the expression, so any number of fields can live on one page. Kinds: text (default), email, url, tel, password, number,
/// date, datetime-local, time, color, textarea, select, checkbox, readonly. For select pass <c>asp-items</c> or &lt;option&gt; children.
/// Dependent fields: <c>show-when-field</c> + <c>show-when-value</c> (comma separated) show the row only while the controlling input has
/// one of those values; hidden inputs are disabled so they neither post nor validate (app.js).
/// Attributes that are not part of this control (readonly, maxlength, min, data-…) go to the input; class goes to the row.
/// </summary>
[HtmlTargetElement("mm-field", Attributes = "asp-for")]
public sealed class FieldTagHelper : TagHelper
{
    private readonly IHtmlGenerator _generator;
    private readonly IStringLocalizer<SharedResource> _l;

    public FieldTagHelper(IHtmlGenerator generator, IStringLocalizer<SharedResource> l)
    {
        _generator = generator;
        _l = l;
    }

    [HtmlAttributeName("asp-for")]
    public ModelExpression For { get; set; } = default!;

    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "text";

    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    [HtmlAttributeName("autocomplete")]
    public string? Autocomplete { get; set; }

    [HtmlAttributeName("rows")]
    public int Rows { get; set; } = 4;

    [HtmlAttributeName("required")]
    public bool Required { get; set; }

    /// <summary>Text after the input, e.g. a unit ("minutes").</summary>
    [HtmlAttributeName("suffix")]
    public string? Suffix { get; set; }

    /// <summary>Password fields: show a button that reveals the typed text.</summary>
    [HtmlAttributeName("reveal")]
    public bool Reveal { get; set; }

    [HtmlAttributeName("asp-items")]
    public IEnumerable<SelectListItem>? Items { get; set; }

    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string kind = (Kind ?? "text").Trim().ToLowerInvariant();
        bool isCheckbox = kind == "checkbox";

        // Everything the author wrote on the tag that is not a tag helper property belongs to the input.
        var passThrough = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string rowClass = string.Empty;
        foreach (TagHelperAttribute attribute in output.Attributes)
        {
            if (string.Equals(attribute.Name, "class", StringComparison.OrdinalIgnoreCase))
            {
                rowClass = " " + attribute.Value;
            }
            else
            {
                passThrough[attribute.Name] = attribute.Value ?? string.Empty;
            }
        }

        output.Attributes.Clear();
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", (isCheckbox ? "form-row form-row--check" : "form-row") + rowClass);
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-when-field", ShowWhenField);
            output.Attributes.SetAttribute("data-show-when-value", ShowWhenValue ?? string.Empty);
        }

        string labelText = Label ?? For.Metadata.DisplayName ?? For.Name;
        TagBuilder label = _generator.GenerateLabel(ViewContext, For.ModelExplorer, For.Name, labelText, htmlAttributes: Required ? new { @class = "is-required" } : null);
        TagBuilder validation = _generator.GenerateValidationMessage(
            ViewContext, For.ModelExplorer, For.Name, message: null, tag: "span", htmlAttributes: new { @class = "field-error" });

        if (isCheckbox)
        {
            TagBuilder checkbox = _generator.GenerateCheckBox(ViewContext, For.ModelExplorer, For.Name, isChecked: null, htmlAttributes: passThrough);
            output.Content.SetHtmlContent("<div class=\"form-check\">");
            output.Content.AppendHtml(checkbox);
            output.Content.AppendHtml(label);
            output.Content.AppendHtml("</div>");
            output.Content.AppendHtml(_generator.GenerateHiddenForCheckbox(ViewContext, For.ModelExplorer, For.Name));
            output.Content.AppendHtml(validation);
            AppendHelp(output);
            return;
        }

        output.Content.SetHtmlContent(label);
        Dictionary<string, object> attributes = BaseAttributes(passThrough);
        IHtmlContent control;

        switch (kind)
        {
            case "textarea":
                control = _generator.GenerateTextArea(ViewContext, For.ModelExplorer, For.Name, Rows, 0, attributes);
                break;

            case "select":
                control = await BuildSelectAsync(output, attributes);
                break;

            case "password":
                control = _generator.GeneratePassword(ViewContext, For.ModelExplorer, For.Name, value: null, attributes);
                if (Reveal)
                {
                    control = Wrap("input-with-action", control, RevealButton());
                }

                break;

            case "readonly":
                string text = For.Model?.ToString() ?? string.Empty;
                control = new HtmlString($"<div class=\"form-static\">{HtmlEncoder.Default.Encode(text)}</div>");
                break;

            default:
                attributes["type"] = MapInputType(kind);
                // GenerateTextBox does not fall back to the model value when value is null, so pass it explicitly.
                control = _generator.GenerateTextBox(ViewContext, For.ModelExplorer, For.Name, value: For.Model, format: null, attributes);
                break;
        }

        if (!string.IsNullOrEmpty(Suffix))
        {
            control = Wrap("input-with-suffix", control, new HtmlString($"<span class=\"input-suffix\">{HtmlEncoder.Default.Encode(Suffix)}</span>"));
        }

        output.Content.AppendHtml(control);
        output.Content.AppendHtml(validation);
        AppendHelp(output);
    }

    private async Task<IHtmlContent> BuildSelectAsync(TagHelperOutput output, Dictionary<string, object> attributes)
    {
        if (Items is not null)
        {
            return _generator.GenerateSelect(ViewContext, For.ModelExplorer, optionLabel: null, For.Name, Items, allowMultiple: false, attributes);
        }

        // Nested <option> children: render the select by hand and mark the option that matches the model value.
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string id = TagBuilder.CreateSanitizedId(fullName, "_");
        string options = HtmlOptionHelper.MarkSelected((await output.GetChildContentAsync()).GetContent(), For.Model?.ToString());
        var extra = new StringBuilder();
        foreach ((string name, object value) in attributes.Where(a => a.Key != "class"))
        {
            extra.Append(' ').Append(HtmlEncoder.Default.Encode(name)).Append("=\"").Append(HtmlEncoder.Default.Encode(value.ToString() ?? string.Empty)).Append('"');
        }

        return new HtmlString(
            $"<select class=\"form-control\" id=\"{HtmlEncoder.Default.Encode(id)}\" name=\"{HtmlEncoder.Default.Encode(fullName)}\"{extra}>{options}</select>");
    }

    private IHtmlContent RevealButton()
    {
        string label = HtmlEncoder.Default.Encode(_l["Show password"].Value);
        return new HtmlString(
            $"<button type=\"button\" class=\"icon-btn input-with-action__btn\" data-reveal title=\"{label}\" aria-label=\"{label}\">" +
            "<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-eye\"/></svg></button>");
    }

    private static IHtmlContent Wrap(string cssClass, IHtmlContent control, IHtmlContent addition)
    {
        var builder = new HtmlContentBuilder();
        builder.AppendHtml($"<div class=\"{cssClass}\">");
        builder.AppendHtml(control);
        builder.AppendHtml(addition);
        builder.AppendHtml("</div>");
        return builder;
    }

    private Dictionary<string, object> BaseAttributes(Dictionary<string, object> passThrough)
    {
        var attributes = new Dictionary<string, object>(passThrough, StringComparer.OrdinalIgnoreCase) { ["class"] = "form-control" };
        if (!string.IsNullOrEmpty(Placeholder))
        {
            attributes["placeholder"] = Placeholder;
        }

        if (!string.IsNullOrEmpty(Autocomplete))
        {
            attributes["autocomplete"] = Autocomplete;
        }

        return attributes;
    }

    private void AppendHelp(TagHelperOutput output)
    {
        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<p class=\"form-help\">{HtmlEncoder.Default.Encode(Help)}</p>");
        }
    }

    private static string MapInputType(string kind) => kind switch
    {
        "email" or "url" or "tel" or "number" or "color" or "date" or "datetime-local" or "time" => kind,
        _ => "text",
    };
}

/// <summary>
/// A searchable select for long lists (single value, or several values bound to a collection). Replaces the native select
/// with a button that opens a dialog — a full-screen dialog on phones. Posting works exactly like a plain
/// <c>&lt;select asp-for&gt;</c> or a checkbox group, so page models need nothing special.
/// Usage: &lt;mm-picker asp-for="Input.RoleIds" asp-items="Model.RoleItems" multiple="true" label="Roles" /&gt;
/// </summary>
[HtmlTargetElement("mm-picker", Attributes = "asp-for,asp-items")]
public sealed class PickerTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public PickerTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("asp-for")]
    public ModelExpression For { get; set; } = default!;

    [HtmlAttributeName("asp-items")]
    public IEnumerable<SelectListItem> Items { get; set; } = default!;

    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    [HtmlAttributeName("multiple")]
    public bool Multiple { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string widgetId = TagBuilder.CreateSanitizedId(fullName, "_") + "__picker";
        string placeholder = Placeholder ?? (Multiple ? _l["Add…"].Value : _l["— None —"].Value);

        IReadOnlyList<SelectListItem> items = Items as IReadOnlyList<SelectListItem> ?? Items.ToList();
        HashSet<string> selected = ResolveSelectedValues();

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "form-row");
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-when-field", ShowWhenField);
            output.Attributes.SetAttribute("data-show-when-value", ShowWhenValue ?? string.Empty);
        }

        string labelText = Label ?? For.Metadata.DisplayName ?? For.Name;
        output.Content.AppendHtml($"<label for=\"{enc.Encode(widgetId)}\">{enc.Encode(labelText)}</label>");

        var widget = new StringBuilder();
        widget.Append($"<div class=\"picker\" id=\"{enc.Encode(widgetId)}\" data-multiple=\"{(Multiple ? "true" : "false")}\" data-name=\"{enc.Encode(fullName)}\"");
        widget.Append($" data-placeholder=\"{enc.Encode(placeholder)}\" data-search-label=\"{enc.Encode(_l["Search…"].Value)}\"");
        widget.Append($" data-done-label=\"{enc.Encode(_l["Done"].Value)}\" data-close-label=\"{enc.Encode(_l["Close"].Value)}\" data-remove-label=\"{enc.Encode(_l["Remove"].Value)}\">");
        widget.Append($"<script type=\"application/json\" class=\"picker__data\">{BuildOptionsJson(items)}</script>");

        if (Multiple)
        {
            widget.Append("<div class=\"picker__chips\">");
            foreach (SelectListItem item in items.Where(i => selected.Contains(i.Value)))
            {
                widget.Append($"<span class=\"picker__chip\" data-value=\"{enc.Encode(item.Value)}\">");
                widget.Append($"<input type=\"hidden\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(item.Value)}\">");
                widget.Append($"<span class=\"picker__chip-text\">{enc.Encode(item.Text)}</span>");
                widget.Append($"<button type=\"button\" class=\"picker__chip-remove\" aria-label=\"{enc.Encode(_l["Remove"].Value)}\" tabindex=\"-1\">×</button></span>");
            }

            widget.Append("</div>");
            widget.Append("<button type=\"button\" class=\"picker__trigger picker__trigger--add form-control\" aria-haspopup=\"dialog\">");
            widget.Append($"<span class=\"picker__label\">{enc.Encode(placeholder)}</span>");
            widget.Append("<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-plus\"/></svg></button>");
        }
        else
        {
            string? current = selected.FirstOrDefault();
            SelectListItem? match = current is null ? null : items.FirstOrDefault(i => string.Equals(i.Value, current, StringComparison.Ordinal));
            widget.Append($"<input type=\"hidden\" class=\"picker__value\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(current ?? string.Empty)}\">");
            widget.Append($"<button type=\"button\" class=\"picker__trigger form-control\" aria-haspopup=\"dialog\"{(match is null ? " data-placeholder-shown=\"true\"" : string.Empty)}>");
            widget.Append($"<span class=\"picker__label\">{enc.Encode(match?.Text ?? placeholder)}</span>");
            widget.Append("<svg class=\"icon\" aria-hidden=\"true\"><use href=\"#i-chevron-down\"/></svg></button>");
        }

        widget.Append("</div>");
        output.Content.AppendHtml(widget.ToString());
        output.Content.AppendHtml($"<span class=\"field-error\" data-valmsg-for=\"{enc.Encode(fullName)}\" data-valmsg-replace=\"true\"></span>");
        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<p class=\"form-help\">{enc.Encode(Help)}</p>");
        }
    }

    private HashSet<string> ResolveSelectedValues()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        object? model = For.Model;
        if (model is null)
        {
            return result;
        }

        if (Multiple && model is IEnumerable enumerable and not string)
        {
            foreach (object? entry in enumerable)
            {
                if (entry is not null)
                {
                    result.Add(Convert.ToString(entry, CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            return result;
        }

        string? scalar = Convert.ToString(model, CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(scalar))
        {
            result.Add(scalar);
        }

        return result;
    }

    // The default serializer escapes '<', so the payload cannot break out of its <script> block.
    private static string BuildOptionsJson(IReadOnlyList<SelectListItem> items)
        => JsonSerializer.Serialize(items.Select(i => new Dictionary<string, string> { ["v"] = i.Value ?? string.Empty, ["t"] = i.Text ?? string.Empty }));
}

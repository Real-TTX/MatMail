using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MatMail.Services;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace MatMail.Controls;

/// <summary>
/// The one form of the application: a POST form (<c>novalidate</c>: the toolkit shows the messages, the browser's bubbles stay away, FORM-05) with the
/// anti-forgery token and, when the server found errors, the summary on top that links to every field (A11Y-04).
/// Fields (<c>mm-field</c>, <c>mm-picker</c>), sections (<c>mm-form-section</c>) and the button row go inside.
/// Usage: &lt;mm-form&gt;…&lt;/mm-form&gt;
/// </summary>
[HtmlTargetElement("mm-form")]
public sealed class FormTagHelper(IHtmlGenerator generator, IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
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
        output.Attributes.SetAttribute("class", "mat-form" + extra);
        output.Attributes.SetAttribute("method", Method);
        output.Attributes.SetAttribute("action", string.IsNullOrEmpty(Action) ? CurrentPage() : Action);
        output.Attributes.SetAttribute("novalidate", "novalidate");

        if (!string.IsNullOrEmpty(Enctype))
        {
            output.Attributes.SetAttribute("enctype", Enctype);
        }

        TagHelperContent children = await output.GetChildContentAsync();
        output.Content.SetHtmlContent(Summary());
        output.Content.AppendHtml(children);

        if (string.Equals(Method, "post", StringComparison.OrdinalIgnoreCase))
        {
            output.PostContent.AppendHtml(generator.GenerateAntiforgery(ViewContext));
        }
    }

    /// <summary>The errors the server found: a list with a link to every field that has one, under a title. Nothing at all when there are none (CODE-06).</summary>
    private string Summary()
    {
        ModelStateDictionary state = ViewContext.ViewData.ModelState;
        if (state.IsValid)
        {
            return string.Empty;
        }

        HtmlEncoder enc = HtmlEncoder.Default;
        var items = new StringBuilder();
        foreach (KeyValuePair<string, ModelStateEntry> entry in state)
        {
            foreach (ModelError error in entry.Value.Errors)
            {
                string text = enc.Encode(string.IsNullOrEmpty(error.ErrorMessage) ? l["The value is not valid."].Value : error.ErrorMessage);
                items.Append(string.IsNullOrEmpty(entry.Key)
                    ? $"<li>{text}</li>"
                    : $"<li><a href=\"#{enc.Encode(TagBuilder.CreateSanitizedId(entry.Key, "_"))}\">{text}</a></li>");
            }
        }

        return "<div class=\"mat-alert mat-alert--danger\" role=\"alert\" data-validation-summary>" + toolkit.IconMarkup("x-circle") +
               $"<div class=\"mat-alert__body\"><strong>{enc.Encode(l["Please check the form."].Value)}</strong><ul class=\"mat-alert__list\">{items}</ul></div></div>";
    }
}

/// <summary>
/// A titled group of fields inside a form: a card with a heading (FORM-10). The fields stand under each other; <c>columns="2"</c> puts them in the two-column grid
/// of the toolkit (FORM-07; a long field spans both with <c>full="true"</c>). Supports dependent visibility like <c>mm-field</c>.
/// Usage: &lt;mm-form-section title="Receiving" description="…" icon="inbox"&gt;…&lt;/mm-form-section&gt;
/// </summary>
[HtmlTargetElement("mm-form-section")]
public sealed class FormSectionTagHelper(Toolkit toolkit) : TagHelper
{
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    [HtmlAttributeName("description")]
    public string? Description { get; set; }

    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    /// <summary>1 (the default) or 2: the fields of the section in one or two columns.</summary>
    [HtmlAttributeName("columns")]
    public int Columns { get; set; } = 1;

    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
        output.TagName = "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-card");
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-if", ShowWhenField);
            output.Attributes.SetAttribute("data-show-value", ShowWhenValue ?? "*");
        }

        TagHelperContent body = await output.GetChildContentAsync();
        if (!string.IsNullOrEmpty(Title))
        {
            string icon = string.IsNullOrEmpty(Icon) ? string.Empty : toolkit.IconMarkup(Icon);
            output.Content.AppendHtml($"<div class=\"mat-card__head\">{icon}<h2 class=\"mat-card__title\">{enc.Encode(Title)}</h2></div>");
        }

        output.Content.AppendHtml("<div class=\"mat-card__body mat-stack\">");
        if (!string.IsNullOrEmpty(Description))
        {
            output.Content.AppendHtml($"<p class=\"mat-muted\">{enc.Encode(Description)}</p>");
        }

        output.Content.AppendHtml(Columns == 2 ? "<div class=\"mat-form__grid\">" : "<div class=\"mat-stack\">");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</div></div>");
    }
}

/// <summary>
/// A complete field built from an <c>asp-for</c> expression: label, control, hint and the error message (FORM-03). Names and ids derive
/// from the expression, so any number of fields can live on one page. Kinds: text (default), email, url, tel, password, number,
/// date, datetime-local, time, color, textarea, richtext, select, checkbox, readonly. For select pass <c>asp-items</c> or &lt;option&gt;
/// children. Richtext is the editor of rte.js on top of a textarea that keeps the HTML: <c>placeholders</c> lists the {{names}} the
/// toolbar offers, <c>preview</c> shows the result below with sample values (<c>sample-body</c>: the text that stands for {{Body}}).
/// Dependent fields: <c>show-when-field</c> + <c>show-when-value</c> (comma separated) show the row only while the controlling input has
/// one of those values; hidden inputs are disabled so they neither post nor validate (FORM-04, the toolkit).
/// Attributes that are not part of this control (readonly, maxlength, min, data-…) go to the input; class goes to the row.
/// </summary>
[HtmlTargetElement("mm-field", Attributes = "asp-for")]
public sealed class FieldTagHelper(IHtmlGenerator generator, IStringLocalizer<SharedResource> l, Toolkit toolkit) : TagHelper
{
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

    /// <summary>Marks the field with the star and lets the toolkit refuse an empty value with a message (FORM-06).</summary>
    [HtmlAttributeName("required")]
    public bool Required { get; set; }

    /// <summary>Text after the input, e.g. a unit ("minutes").</summary>
    [HtmlAttributeName("suffix")]
    public string? Suffix { get; set; }

    /// <summary>Password fields: show a button that reveals the typed text.</summary>
    [HtmlAttributeName("reveal")]
    public bool Reveal { get; set; }

    /// <summary>In a two-column section: the field spans both columns (a long text, a rich text editor).</summary>
    [HtmlAttributeName("full")]
    public bool Full { get; set; }

    [HtmlAttributeName("asp-items")]
    public IEnumerable<SelectListItem>? Items { get; set; }

    /// <summary>Rich text: the {{placeholders}} the toolbar offers, comma separated.</summary>
    [HtmlAttributeName("placeholders")]
    public string? Placeholders { get; set; }

    /// <summary>Rich text: show the result below, with sample values for the placeholders.</summary>
    [HtmlAttributeName("preview")]
    public bool Preview { get; set; }

    /// <summary>Rich text: the text that stands for {{Body}} in the preview.</summary>
    [HtmlAttributeName("sample-body")]
    public string? SampleBody { get; set; }

    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;
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

        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string id = TagBuilder.CreateSanitizedId(fullName, "_");
        string? error = ViewContext.ViewData.ModelState.TryGetValue(fullName, out ModelStateEntry? entry) ? entry.Errors.FirstOrDefault()?.ErrorMessage : null;
        bool invalid = !string.IsNullOrEmpty(error);

        output.Attributes.Clear();
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-field" + (invalid ? " is-invalid" : string.Empty) + (Full ? " mat-field--full" : string.Empty) + rowClass);
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-if", ShowWhenField);
            output.Attributes.SetAttribute("data-show-value", ShowWhenValue ?? "*");
        }

        string labelText = Label ?? For.Metadata.DisplayName ?? For.Name;
        string errorId = id + "-err";
        string star = Required ? " <span class=\"mat-req\" aria-hidden=\"true\">*</span>" : string.Empty;
        string errorHtml = $"<span class=\"mat-error\" id=\"{enc.Encode(errorId)}\" aria-live=\"polite\">{enc.Encode(error ?? string.Empty)}</span>";

        if (isCheckbox)
        {
            TagBuilder checkbox = generator.GenerateCheckBox(ViewContext, For.ModelExplorer, For.Name, isChecked: null, htmlAttributes: passThrough);
            output.Content.SetHtmlContent("<label class=\"mat-check\">");
            output.Content.AppendHtml(checkbox);
            output.Content.AppendHtml($"<span class=\"mat-check__text\">{enc.Encode(labelText)}{(Help is null ? string.Empty : $"<small class=\"mat-muted\">{enc.Encode(Help)}</small>")}</span></label>");
            output.Content.AppendHtml(generator.GenerateHiddenForCheckbox(ViewContext, For.ModelExplorer, For.Name));
            output.Content.AppendHtml(errorHtml);
            return;
        }

        output.Content.SetHtmlContent($"<label class=\"mat-label\" for=\"{enc.Encode(id)}\">{enc.Encode(labelText)}{star}</label>");
        Dictionary<string, object> attributes = BaseAttributes(passThrough, kind, invalid, errorId);
        IHtmlContent control;
        IHtmlContent? afterHelp = null;

        switch (kind)
        {
            case "textarea":
                control = generator.GenerateTextArea(ViewContext, For.ModelExplorer, For.Name, Rows, 0, attributes);
                break;

            case "richtext":
                attributes["data-rich-text"] = "true";
                attributes["data-placeholders"] = Placeholders ?? string.Empty;
                attributes["data-texts"] = JsonSerializer.Serialize(RichTextTexts());
                attributes["spellcheck"] = "false";
                control = generator.GenerateTextArea(ViewContext, For.ModelExplorer, For.Name, Rows > 4 ? Rows : 10, 0, attributes);
                afterHelp = Preview ? PreviewFrame() : null;
                break;

            case "select":
                control = await BuildSelectAsync(output, attributes);
                break;

            case "password":
                control = generator.GeneratePassword(ViewContext, For.ModelExplorer, For.Name, value: null, attributes);
                if (Reveal)
                {
                    control = Wrap("mat-input-icon mat-input-icon--end", control, RevealButton(id));
                }

                break;

            case "readonly":
                string text = For.Model?.ToString() ?? string.Empty;
                control = new HtmlString($"<input class=\"mat-input mat-input--plain\" id=\"{enc.Encode(id)}\" value=\"{enc.Encode(text)}\" readonly tabindex=\"-1\" />");
                break;

            default:
                attributes["type"] = MapInputType(kind);
                // GenerateTextBox does not fall back to the model value when value is null, so pass it explicitly.
                control = generator.GenerateTextBox(ViewContext, For.ModelExplorer, For.Name, value: For.Model, format: null, attributes);
                break;
        }

        if (!string.IsNullOrEmpty(Suffix))
        {
            control = Wrap("mat-input-group", control, new HtmlString($"<span class=\"mat-input-addon\">{enc.Encode(Suffix)}</span>"));
        }

        output.Content.AppendHtml(control);
        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<span class=\"mat-hint\">{enc.Encode(Help)}</span>");
        }

        output.Content.AppendHtml(errorHtml);
        if (afterHelp is not null)
        {
            output.Content.AppendHtml(afterHelp);
        }
    }

    private async Task<IHtmlContent> BuildSelectAsync(TagHelperOutput output, Dictionary<string, object> attributes)
    {
        if (Items is not null)
        {
            return generator.GenerateSelect(ViewContext, For.ModelExplorer, optionLabel: null, For.Name, Items, allowMultiple: false, attributes);
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
            $"<select class=\"mat-select\" id=\"{HtmlEncoder.Default.Encode(id)}\" name=\"{HtmlEncoder.Default.Encode(fullName)}\"{extra}>{options}</select>");
    }

    private IHtmlContent RevealButton(string inputId)
    {
        string label = HtmlEncoder.Default.Encode(l["Show password"].Value);
        return new HtmlString(
            $"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm mat-input-icon__btn\" data-password-toggle=\"{HtmlEncoder.Default.Encode(inputId)}\" aria-pressed=\"false\" aria-label=\"{label}\" data-tip=\"{label}\">" +
            toolkit.IconMarkup("eye", "mat-toggle-on") + toolkit.IconMarkup("eye-off", "mat-toggle-off") + "</button>");
    }

    /// <summary>The texts of the editor's toolbar and messages (rte.js), in the language of the page.</summary>
    private Dictionary<string, string> RichTextTexts() => new()
    {
        ["content"] = l["Content"].Value, ["bold"] = l["Bold"].Value, ["italic"] = l["Italic"].Value, ["underline"] = l["Underline"].Value,
        ["size"] = l["Text size"].Value, ["sizeSmall"] = l["Small"].Value, ["sizeNormal"] = l["Normal"].Value, ["sizeLarge"] = l["Large"].Value,
        ["sizeHuge"] = l["Very large"].Value, ["color"] = l["Text colour"].Value, ["bulletList"] = l["Bulleted list"].Value,
        ["numberList"] = l["Numbered list"].Value, ["link"] = l["Link"].Value, ["linkPrompt"] = l["Web address (or mailto:)"].Value,
        ["picture"] = l["Insert picture"].Value, ["rule"] = l["Horizontal line"].Value, ["clear"] = l["Clear formatting"].Value,
        ["placeholder"] = l["Insert placeholder"].Value, ["source"] = l["HTML source"].Value, ["pictureWidth"] = l["Width in pixels"].Value,
        ["pictureRemove"] = l["Remove picture"].Value, ["pictureType"] = l["Only PNG, JPEG, GIF and WebP pictures can be inserted."].Value,
        ["pictureTooLarge"] = l["The picture is too large, even after shrinking it."].Value, ["close"] = l["Close"].Value, ["ok"] = l["OK"].Value,
        ["cancel"] = l["Cancel"].Value,
    };

    /// <summary>The frame that shows the HTML with sample values; sandboxed, so nothing in it can run.</summary>
    private IHtmlContent PreviewFrame()
    {
        string tenant = ViewContext.HttpContext.User.FindFirst(AppClaims.TenantName)?.Value ?? "Example GmbH";
        var sample = new Dictionary<string, string>
        {
            ["FullName"] = "Dr. Max Mustermann", ["Salutation"] = "Mr", ["Title"] = "Dr.", ["FirstName"] = "Max", ["LastName"] = "Mustermann",
            ["DisplayName"] = "Max Mustermann", ["Email"] = "max@example.com", ["JobTitle"] = l["Managing director"].Value, ["Department"] = l["Management"].Value,
            ["Phone"] = "+49 123 456789", ["Mobile"] = "+49 170 1234567", ["Fax"] = "+49 123 456780", ["Website"] = "https://www.example.com", ["Tenant"] = tenant,
        };

        // {{Body}} is markup (the sample message); everything else is text and gets encoded by rte.js.
        string body = string.Join("<br>", (SampleBody ?? string.Empty).Split('\n').Select(line => HtmlEncoder.Default.Encode(line.TrimEnd('\r'))));
        string title = HtmlEncoder.Default.Encode(l["Preview"].Value);
        string json = HtmlEncoder.Default.Encode(JsonSerializer.Serialize(new { values = sample, body }));
        return new HtmlString($"<span class=\"mat-label\">{title}</span><iframe class=\"mm-signature-preview\" sandbox title=\"{title}\" data-rte-preview data-sample=\"{json}\"></iframe>");
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

    private Dictionary<string, object> BaseAttributes(Dictionary<string, object> passThrough, string kind, bool invalid, string errorId)
    {
        string cssClass = kind switch { "select" => "mat-select", "textarea" or "richtext" => "mat-textarea", _ => "mat-input" };
        var attributes = new Dictionary<string, object>(passThrough, StringComparer.OrdinalIgnoreCase) { ["class"] = cssClass };
        if (!string.IsNullOrEmpty(Placeholder))
        {
            attributes["placeholder"] = Placeholder;
        }

        if (!string.IsNullOrEmpty(Autocomplete))
        {
            attributes["autocomplete"] = Autocomplete;
        }

        if (Required)
        {
            attributes["required"] = "required";
            attributes["data-msg-required"] = l["Please fill in this field."].Value;
        }

        if (invalid)
        {
            attributes["aria-invalid"] = "true";
            attributes["aria-describedby"] = errorId;
        }

        return attributes;
    }

    private static string MapInputType(string kind) => kind switch
    {
        "email" or "url" or "tel" or "number" or "color" or "date" or "datetime-local" or "time" => kind,
        _ => "text",
    };
}

/// <summary>
/// A choice from a list. One value: the picker of the toolkit (a field that opens a panel, a search for a long list; FORM-15), which posts the key of the option.
/// <c>multiple="true"</c>: a group of checks (several yes-or-no choices), which posts one value per check, so it binds to a collection like any checkbox group.
/// Usage: &lt;mm-picker asp-for="Input.RoleIds" asp-items="Model.RoleItems" multiple="true" label="Roles" /&gt;
/// </summary>
[HtmlTargetElement("mm-picker", Attributes = "asp-for,asp-items")]
public sealed class PickerTagHelper(IStringLocalizer<SharedResource> l) : TagHelper
{
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
        string id = TagBuilder.CreateSanitizedId(fullName, "_");
        IReadOnlyList<SelectListItem> items = Items as IReadOnlyList<SelectListItem> ?? Items.ToList();
        HashSet<string> selected = ResolveSelectedValues();
        string? error = ViewContext.ViewData.ModelState.TryGetValue(fullName, out ModelStateEntry? entry) ? entry.Errors.FirstOrDefault()?.ErrorMessage : null;
        string labelText = Label ?? For.Metadata.DisplayName ?? For.Name;

        output.TagName = Multiple ? "fieldset" : "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-field" + (Multiple ? " mat-fieldset" : string.Empty) + (string.IsNullOrEmpty(error) ? string.Empty : " is-invalid"));
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-if", ShowWhenField);
            output.Attributes.SetAttribute("data-show-value", ShowWhenValue ?? "*");
        }

        if (Multiple)
        {
            output.Content.AppendHtml($"<legend class=\"mat-legend\">{enc.Encode(labelText)}</legend><div class=\"mat-checks\">");
            int index = 0;
            foreach (SelectListItem item in items)
            {
                output.Content.AppendHtml(
                    $"<label class=\"mat-check\"><input type=\"checkbox\" id=\"{enc.Encode(id)}_{index++}\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(item.Value)}\"{(selected.Contains(item.Value) ? " checked" : string.Empty)} />" +
                    $"<span class=\"mat-check__text\">{enc.Encode(item.Text)}</span></label>");
            }

            output.Content.AppendHtml("</div>");
        }
        else
        {
            string placeholder = Placeholder ?? l["— None —"].Value;
            output.Content.AppendHtml($"<label class=\"mat-label\" for=\"{enc.Encode(id)}\">{enc.Encode(labelText)}</label>");
            output.Content.AppendHtml($"<div class=\"mat-picker\" data-picker=\"select\" data-placeholder=\"{enc.Encode(placeholder)}\" data-clearable{(items.Count > 8 ? " data-search" : string.Empty)}>");
            output.Content.AppendHtml($"<input class=\"mat-picker__input\" id=\"{enc.Encode(id)}\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(selected.FirstOrDefault() ?? string.Empty)}\" />");
            output.Content.AppendHtml("<div class=\"mat-picker__options\" hidden>");
            foreach (SelectListItem item in items)
            {
                output.Content.AppendHtml($"<button type=\"button\" class=\"mat-picker__option\" data-value=\"{enc.Encode(item.Value)}\"><span class=\"mat-picker__text\"><span class=\"mat-picker__name\">{enc.Encode(item.Text)}</span></span></button>");
            }

            output.Content.AppendHtml("</div></div>");
        }

        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<span class=\"mat-hint\">{enc.Encode(Help)}</span>");
        }

        output.Content.AppendHtml($"<span class=\"mat-error\" id=\"{enc.Encode(id)}-err\" aria-live=\"polite\">{enc.Encode(error ?? string.Empty)}</span>");
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
}

using Ganss.Xss;

namespace MatMail.Messaging;

/// <summary>
/// HTML that is put into the live page, not into a sandboxed frame: the message being written in the web client (a signature, the
/// quoted original of a reply, a draft) and the rich text editor of the administration. What a mail reader may show inside its sandbox
/// is more than what may sit in the page itself: scripts are not the only danger. A <c>position: fixed</c> block with a form in it
/// is a fake sign-in over the whole window, and named inputs inside the form of an editor page change what that page posts. So on top
/// of the defaults of the sanitiser (no scripts, no event handlers, no script addresses) there are no forms and controls, no
/// positioning, no names that could replace what the page's own scripts look up, and nothing that makes elements focusable or editable
/// on their own.
/// </summary>
public static class EditorHtml
{
    private static readonly string[] BlockedTags =
    {
        "form", "input", "button", "select", "option", "optgroup", "datalist", "textarea", "fieldset", "legend", "label", "output",
        "meter", "progress", "details", "summary", "dialog", "menu", "menuitem",
    };

    private static readonly string[] BlockedCssProperties =
    {
        "position", "z-index", "top", "left", "right", "bottom", "inset", "inset-block", "inset-inline", "pointer-events", "clip", "clip-path",
        "transform", "translate", "rotate", "scale", "filter", "backdrop-filter", "mask", "cursor", "content", "will-change", "isolation",
    };

    private static readonly string[] BlockedAttributes =
    {
        "name", "action", "method", "enctype", "novalidate", "accesskey", "tabindex", "contenteditable", "autocomplete", "autosave", "autofocus",
        "challenge", "keytype", "for", "form", "formaction", "list", "dirname", "pattern", "placeholder", "prompt", "radiogroup", "readonly", "required",
        "checked", "disabled", "multiple", "selected", "min", "max", "step", "maxlength", "open", "draggable",
    };

    /// <summary>
    /// A sanitiser for HTML that goes into the page. <paramref name="extraSchemes"/> are the address schemes the content may use besides
    /// the usual (http, https and a few): "mailto", "tel", "cid", "data".
    /// </summary>
    public static HtmlSanitizer CreateSanitizer(params string[] extraSchemes)
    {
        var sanitizer = new HtmlSanitizer();
        foreach (string scheme in extraSchemes)
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        foreach (string tag in BlockedTags)
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        foreach (string property in BlockedCssProperties)
        {
            sanitizer.AllowedCssProperties.Remove(property);
        }

        foreach (string attribute in BlockedAttributes)
        {
            sanitizer.AllowedAttributes.Remove(attribute);
        }

        return sanitizer;
    }

    /// <summary>The HTML as it may enter the page.</summary>
    public static string Clean(string html, params string[] extraSchemes) => CreateSanitizer(extraSchemes).Sanitize(html);
}

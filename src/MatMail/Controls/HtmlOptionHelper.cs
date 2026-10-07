using System.Text.RegularExpressions;

namespace MatMail.Controls;

/// <summary>
/// Marks the &lt;option&gt; whose <c>value</c> equals the selected value with <c>selected</c>. Used by the toolbar select and the
/// select field when a page supplies raw &lt;option&gt; children. Options must carry an explicit value attribute.
/// </summary>
internal static partial class HtmlOptionHelper
{
    [GeneratedRegex("<option\\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex OptionTag();

    [GeneratedRegex("value\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttribute();

    [GeneratedRegex("(^|\\s)selected(\\s|=|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadySelected();

    public static string MarkSelected(string optionsHtml, string? selectedValue)
    {
        if (string.IsNullOrEmpty(optionsHtml) || selectedValue is null)
        {
            return optionsHtml;
        }

        return OptionTag().Replace(optionsHtml, match =>
        {
            string attributes = match.Groups[1].Value;
            if (AlreadySelected().IsMatch(attributes))
            {
                return match.Value;
            }

            Match value = ValueAttribute().Match(attributes);
            return value.Success && value.Groups[1].Value == selectedValue ? $"<option{attributes} selected>" : match.Value;
        });
    }
}

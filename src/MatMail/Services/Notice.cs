using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatMail.Services;

/// <summary>How a notice reads: a result, a hint, a warning or a failure.</summary>
public enum NoticeKind
{
    Ok,
    Info,
    Warn,
    Danger,
}

/// <summary>
/// One convention for the short "what just happened" message above a page. Page models call <see cref="Notify(PageModel, string?, NoticeKind)"/>
/// (survives the redirect) or <see cref="NotifyNow(PageModel, string?, NoticeKind)"/> (same request); the layout renders it.
/// Field validation stays with <c>asp-validation-summary</c>.
/// </summary>
public static class NoticeExtensions
{
    public const string TextKey = "Notice";
    public const string KindKey = "NoticeKind";

    public static void Notify(this PageModel page, string? text, NoticeKind kind = NoticeKind.Ok)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        page.TempData[TextKey] = text;
        page.TempData[KindKey] = kind.ToString();
    }

    public static void NotifyNow(this PageModel page, string? text, NoticeKind kind = NoticeKind.Ok)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        page.ViewData[TextKey] = text;
        page.ViewData[KindKey] = kind.ToString();
    }

    public static void Notify(this PageModel page, bool ok, string? text) => page.Notify(text, ok ? NoticeKind.Ok : NoticeKind.Danger);

    public static void NotifyNow(this PageModel page, bool ok, string? text) => page.NotifyNow(text, ok ? NoticeKind.Ok : NoticeKind.Danger);
}

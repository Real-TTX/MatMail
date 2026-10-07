using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatMail.Services;

/// <summary>The first step of a web sign-in that waits for the code: who passed the password, and what the sign-in is meant to do afterwards.</summary>
/// <param name="TenantSlug">The tenant sign-in page the person came from (/t/name), so the second step looks the same.</param>
public sealed record PendingSignIn(long UserId, bool Remember, string? ReturnUrl, string? TenantSlug);

/// <summary>
/// The proof that a password was right, carried from the password page to the page that asks for the code. A cookie that only the
/// server can read or make (data protection, bound to this purpose), HttpOnly, SameSite Strict, valid for five minutes. It is not a
/// session: it opens nothing but the code page, and without a right code it leads nowhere.
/// </summary>
public sealed class TwoFactorTicket
{
    public const string CookieName = "MatMail.TwoFactor";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string CookiePath = "/Account";

    private readonly ITimeLimitedDataProtector _protector;

    public TwoFactorTicket(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("MatMail.TwoFactorTicket.v1").ToTimeLimitedDataProtector();

    public void Issue(HttpContext http, PendingSignIn pending) => Issue(http, pending, Lifetime);

    internal void Issue(HttpContext http, PendingSignIn pending, TimeSpan lifetime)
    {
        // What the address bar sent along is not allowed to make the cookie too big for the browser.
        PendingSignIn bounded = pending with { ReturnUrl = Bounded(pending.ReturnUrl, 1000), TenantSlug = Bounded(pending.TenantSlug, 64) };
        string value = _protector.Protect(JsonSerializer.Serialize(bounded), lifetime);
        http.Response.Cookies.Append(CookieName, value, Options(http, lifetime));
    }

    private static string? Bounded(string? text, int maxLength) => text is { Length: > 0 } && text.Length <= maxLength ? text : null;

    /// <summary>The pending sign-in of this request, or null when there is none, it expired or it was tampered with.</summary>
    public PendingSignIn? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingSignIn>(_protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, Options(http, null));

    private static CookieOptions Options(HttpContext http, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = CookiePath,
        MaxAge = lifetime,
    };
}

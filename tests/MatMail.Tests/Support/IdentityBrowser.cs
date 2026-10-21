using System.Net;
using System.Text.RegularExpressions;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace MatMail.Tests.Support;

/// <summary>
/// A browser with its own cookies against a <see cref="PlatformFactory"/>; the callback of MatIdentity (<c>/signin-oidc</c>) is run in the app's
/// services with <see cref="MatIdentitySignIn.CompleteSignInAsync"/> and the cookies it sets are handed over, so no MatIdentity has to run.
/// </summary>
internal sealed partial class IdentityBrowser
{
    private static readonly Uri Base = new("http://localhost");
    private readonly PlatformFactory _site;
    private readonly CookieContainer _cookies = new();

    public IdentityBrowser(PlatformFactory site)
    {
        _site = site;
        Client = site.CreateDefaultClient(Base, new CookieContainerHandler(_cookies));
    }

    public HttpClient Client { get; }

    public bool HasPendingLink => _cookies.GetCookies(Base)[MatIdentityPendingLink.CookieName] is { Expired: false } cookie && cookie.Value.Length > 0;

    public void SetPendingLink(string value) => _cookies.Add(Base, new Cookie(MatIdentityPendingLink.CookieName, value, "/"));

    /// <summary>What <c>/signin-oidc</c> does after MatIdentity signed the person in; the cookies it sets go into this browser.</summary>
    public async Task<string> SignInThroughIdentityAsync(MatIdentityPerson person)
    {
        using IServiceScope scope = _site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = http;
        try
        {
            string target = await MatIdentitySignIn.CompleteSignInAsync(http, person);
            foreach (SetCookieHeaderValue cookie in SetCookieHeaderValue.ParseList(http.Response.Headers.SetCookie.Select(c => c ?? string.Empty).ToList()))
            {
                _cookies.Add(Base, new Cookie(cookie.Name.Value!, cookie.Value.Value, "/"));
            }

            return target;
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    public async Task<HttpResponseMessage> SignInLocallyAsync(string login, string password)
    {
        string page = await Client.GetStringAsync("/Account/Login?local=1");
        return await Client.PostAsync("/Account/Login?local=1", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.LoginName"] = login,
            ["Input.Password"] = password,
            ["Input.Remember"] = "false",
        }));
    }

    /// <summary>The anti-forgery token of a page.</summary>
    public static string Token(string html) => TokenRegex().Match(html).Groups[1].Value;

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

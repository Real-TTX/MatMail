using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatMail.Configuration;
using MatMail.Tests.Support;
using MatPlatform;
using Microsoft.AspNetCore.WebUtilities;

namespace MatMail.Tests;

/// <summary>The platform contract of MatDesign docs/PLATFORM.md as MatMail answers it: shapes, status codes, access, the settings, the actions.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class PlatformApiTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    private static readonly string[] Protected = ["/api/state", "/api/state/users", "/api/configure"];

    [DbFact]
    public async Task Health_answers_without_a_key_and_the_old_address_too()
    {
        HttpResponseMessage response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("matmail", doc.RootElement.GetProperty("app").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("version").GetString()));
        Assert.Equal(
            ["database", "setup", "accounts", "backup"],
            doc.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.All(doc.RootElement.GetProperty("checks").EnumerateArray(), c => Assert.Equal("ok", c.GetProperty("status").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await site.CreateClient().GetAsync("/healthz")).StatusCode);
    }

    [DbFact]
    public async Task The_definition_answers_without_a_key_and_with_an_etag()
    {
        HttpClient client = site.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string etag = response.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(request)).StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        Assert.Empty(MatPlatformValidator.Validate(json, Sprite()));

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        JsonElement app = root.GetProperty("app");
        Assert.Equal("matmail", app.GetProperty("id").GetString());
        Assert.Equal("MatMail", app.GetProperty("name").GetString());
        Assert.Equal("#475569", app.GetProperty("accent").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));
        Assert.Equal(["overview", "settings"], root.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(["admin", "tenant-admin", "user"], root.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("key").GetString()));
    }

    [DbFact]
    public async Task The_definition_follows_accept_language()
    {
        HttpClient client = site.CreateClient();
        using var german = new HttpRequestMessage(HttpMethod.Get, "/api");
        german.Headers.TryAddWithoutValidation("Accept-Language", "de-DE");
        using var english = new HttpRequestMessage(HttpMethod.Get, "/api");
        english.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using JsonDocument de = JsonDocument.Parse(await (await client.SendAsync(german)).Content.ReadAsStringAsync());
        using JsonDocument en = JsonDocument.Parse(await (await client.SendAsync(english)).Content.ReadAsStringAsync());
        Assert.Equal("Übersicht", de.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
        Assert.Equal("Overview", en.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
    }

    [DbFact]
    public async Task State_configure_and_actions_need_the_key()
    {
        HttpClient anonymous = site.CreateClient();
        foreach (string path in Protected)
        {
            HttpResponseMessage response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/configure", new { values = new { hostname = "x.example" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/actions/retryQueue", null)).StatusCode);

        HttpClient wrong = site.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/state")).StatusCode);

        // A bearer token is no way in while MatIdentity is not configured.
        HttpClient bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "abc.def.ghi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);

        // The API of the web client is not part of the platform: still closed without a session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api().GetAsync("/api/mail/bootstrap")).StatusCode);
    }

    [DbFact]
    public async Task State_answers_every_tile_and_one_value_by_key()
    {
        HttpClient api = site.Api();
        using JsonDocument definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        using JsonDocument state = JsonDocument.Parse(await api.GetStringAsync("/api/state"));
        JsonElement values = state.RootElement.GetProperty("values");
        Assert.True(state.RootElement.TryGetProperty("time", out _));

        foreach (string key in definition.RootElement.GetProperty("state").EnumerateArray().Select(s => s.GetProperty("key").GetString()!))
        {
            Assert.True(values.TryGetProperty(key, out JsonElement value), $"No value for {key}");
            Assert.True(value.TryGetProperty("value", out _), $"{key} has no value field");
        }

        // The installation from the environment: one tenant, its administrator, nothing sent or received yet.
        Assert.Equal(1, values.GetProperty("tenants").GetProperty("value").GetInt32());
        Assert.Equal(1, values.GetProperty("users").GetProperty("value").GetInt32());
        Assert.Equal(0, values.GetProperty("queue").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, values.GetProperty("lastSent").GetProperty("value").ValueKind);

        HttpResponseMessage one = await api.GetAsync("/api/state/users");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using (JsonDocument doc = JsonDocument.Parse(await one.Content.ReadAsStringAsync()))
        {
            Assert.Equal("users", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("value").GetInt32());
        }

        HttpResponseMessage unknown = await api.GetAsync("/api/state/nothing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (JsonDocument doc = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("nothing", out _));
        }
    }

    [DbFact]
    public async Task Configure_answers_every_field_and_never_a_secret()
    {
        HttpClient api = site.Api();
        using JsonDocument definition = JsonDocument.Parse(await api.GetStringAsync("/api"));
        using JsonDocument configuration = JsonDocument.Parse(await api.GetStringAsync("/api/configure"));
        JsonElement values = configuration.RootElement.GetProperty("values");

        foreach (JsonElement field in definition.RootElement.GetProperty("configure").EnumerateArray())
        {
            string key = field.GetProperty("key").GetString()!;
            Assert.True(values.TryGetProperty(key, out JsonElement value), $"No value for {key}");
            if (field.GetProperty("type").GetString() == "secret")
            {
                Assert.Equal(["isSet"], value.EnumerateObject().Select(p => p.Name));
            }
        }

        Assert.Equal("en-US", values.GetProperty("culture").GetString());
        string raw = configuration.RootElement.GetRawText() + definition.RootElement.GetRawText();
        Assert.DoesNotContain(PlatformFactory.ApiKey, raw);
        Assert.DoesNotContain(PlatformFactory.AdminPassword, raw);
        Assert.DoesNotContain("Password", raw, StringComparison.Ordinal);   // nothing of the database settings
    }

    [DbFact]
    public async Task Configure_checks_every_value_saves_nothing_when_one_is_wrong_and_writes_the_file()
    {
        HttpClient api = site.Api();

        HttpResponseMessage bad = await api.PutAsJsonAsync("/api/configure", new { values = new { hostname = "mail.example.org", timeZone = "Mars/Olympus" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (JsonDocument errors = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()))
        {
            Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("timeZone", out _));
        }

        Assert.Equal("localhost", AppConfigLoader.LoadFile(site.DataDir).Server.Hostname);

        foreach (object wrong in new object[]
                 {
                     new { nothing = 1 },
                     new { hostname = "" },
                     new { hostname = "two words" },
                     new { culture = "fr-FR" },
                     new { maxUploadMb = 0 },
                     new { maxUploadMb = 1.5 },
                     new { maxMessageSizeMb = 99999 },
                     new { trashDays = -1 },
                     new { allowDirectDelivery = "yes" },
                 })
        {
            HttpResponseMessage response = await api.PutAsJsonAsync("/api/configure", new { values = wrong });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{JsonSerializer.Serialize(wrong)}: {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/configure", JsonContent.Create(new { nothing = 1 }))).StatusCode);

        HttpResponseMessage good = await api.PutAsJsonAsync("/api/configure", new
        {
            values = new { hostname = "mail.example.org", culture = "de-DE", timeZone = "Europe/Vienna", maxUploadMb = 20, allowDirectDelivery = false, trashDays = 0 },
        });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using (JsonDocument saved = JsonDocument.Parse(await good.Content.ReadAsStringAsync()))
        {
            JsonElement values = saved.RootElement.GetProperty("values");
            Assert.Equal("mail.example.org", values.GetProperty("hostname").GetString());
            Assert.Equal("de-DE", values.GetProperty("culture").GetString());
            Assert.Equal(20, values.GetProperty("maxUploadMb").GetInt32());
            Assert.False(values.GetProperty("allowDirectDelivery").GetBoolean());
            Assert.Equal(0, values.GetProperty("trashDays").GetInt32());
            Assert.Equal(30, values.GetProperty("junkDays").GetInt32());
        }

        AppConfig file = AppConfigLoader.LoadFile(site.DataDir);
        Assert.Equal("mail.example.org", file.Server.Hostname);
        Assert.Equal("Europe/Vienna", file.Display.TimeZone);
        Assert.Equal(50, file.Smtp.MaxMessageSizeMb);   // what was not sent stays

        HttpResponseMessage reset = await api.PutAsJsonAsync("/api/configure", new
        {
            values = new { hostname = "localhost", culture = "en-US", timeZone = "Europe/Berlin", maxUploadMb = 50, allowDirectDelivery = true, trashDays = 30 },
        });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [DbFact]
    public async Task Actions_run_a_link_is_no_action_and_an_unknown_one_is_404()
    {
        HttpClient api = site.Api();

        HttpResponseMessage retry = await api.PostAsync("/api/actions/retryQueue", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using (JsonDocument doc = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.StartsWith("0 ", doc.RootElement.GetProperty("message").GetString());
        }

        // The synchronisation is switched off in this installation: the action says so instead of pretending.
        HttpResponseMessage fetch = await api.PostAsync("/api/actions/fetchMail", null);
        Assert.Equal(HttpStatusCode.Conflict, fetch.StatusCode);
        using (JsonDocument doc = JsonDocument.Parse(await fetch.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/openAdmin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/actions/nothing", null)).StatusCode);
    }

    private static IReadOnlySet<string> Sprite()
        => MatPlatformValidator.ReadSprite(Path.Combine(Root(), "src", "MatMail", "wwwroot", "matui", "matui-icons.svg"));

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}

/// <summary>Without <c>MAT_API_KEY</c> the key is off: the protected endpoints answer 401 to every key.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class PlatformWithoutKeyTests(NoKeyPlatformFactory site) : IClassFixture<NoKeyPlatformFactory>
{
    [DbFact]
    public async Task Every_protected_endpoint_is_401_and_the_definition_says_so()
    {
        HttpClient client = site.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PlatformFactory.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/actions/retryQueue", null)).StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local sign-in form, as before.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class LocalSignInTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    [DbFact]
    public async Task The_sign_in_page_is_the_local_form()
    {
        HttpResponseMessage response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);
    }

    [DbFact]
    public async Task A_page_that_needs_a_sign_in_sends_to_the_local_form_and_the_form_signs_in()
    {
        HttpClient browser = site.Browser();
        HttpResponseMessage response = await browser.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString);

        await SignIn.LocalAsync(browser, "/Account/Login");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Admin")).StatusCode);

        // Nothing answers the callback address of MatIdentity: like any unknown address it is 404 for somebody signed in (without a session
        // every address that is no page of its own leads to the sign-in, so only the signed-in browser can tell).
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/signin-oidc")).StatusCode);

        HttpResponseMessage signedOut = await SignIn.PostAsync(browser, "/Account", "/Account/Logout");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Account/Login", signedOut.Headers.Location!.OriginalString);
    }
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the automatic sign-in at MatIdentity, the local way in with <c>?local=1</c>.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class IdentitySignInTests(IdentityPlatformFactory site) : IClassFixture<IdentityPlatformFactory>
{
    [DbFact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        HttpResponseMessage response = await site.Browser().GetAsync("/Account/Login?returnUrl=%2FAdmin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Uri location = response.Headers.Location!;
        Assert.StartsWith(IdentityPlatformFactory.Authority + "/connect/authorize", location.OriginalString);

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matmail", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
        Assert.Equal(["openid", "profile", "email", "roles", "offline_access"], query["scope"].ToString().Split(' '));
    }

    [DbFact]
    public async Task Behind_a_tls_proxy_the_address_for_matidentity_is_https()
    {
        // MatPlatform 1.2.0: X-Forwarded-Proto of Matcad gives the scheme (only the scheme), so the redirect_uri is https and the cookies are Secure.
        HttpClient browser = site.Browser();
        browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        HttpResponseMessage response = await browser.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.StartsWith("https://", query["redirect_uri"].ToString());

        HttpResponseMessage plain = await site.Browser().GetAsync("/Account/Login");
        Assert.StartsWith("http://", QueryHelpers.ParseQuery(plain.Headers.Location!.Query)["redirect_uri"].ToString());
    }

    [DbFact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        HttpClient browser = site.Browser();
        HttpResponseMessage first = await browser.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        string login = first.Headers.Location!.OriginalString;
        Assert.Contains("/Account/Login", login);

        HttpResponseMessage second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityPlatformFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [DbFact]
    public async Task The_local_form_stays_reachable_works_and_the_sign_out_does_not_go_straight_back()
    {
        HttpClient browser = site.Browser();
        HttpResponseMessage page = await browser.GetAsync("/Account/Login?local=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("Sign in with MatIdentity", html);

        await SignIn.LocalAsync(browser, "/Account/Login?local=1");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Admin")).StatusCode);

        // A local session has no ID token: signed out here only, and the sign-in page does not sign in again at once.
        HttpResponseMessage signedOut = await SignIn.PostAsync(browser, "/Account", "/Account/Logout");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Account/Login?signedOut=1", signedOut.Headers.Location!.OriginalString);

        HttpResponseMessage after = await browser.GetAsync("/Account/Login?signedOut=1");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        string afterHtml = await after.Content.ReadAsStringAsync();
        Assert.Contains("Sign in with MatIdentity", afterHtml);
        Assert.DoesNotContain("id=\"login-form\"", afterHtml);
    }

    [DbFact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using JsonDocument doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.Equal(IdentityPlatformFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        HttpClient bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);
    }
}

/// <summary>MatIdentity is configured but down: the sign-in page falls back to the local form with a message instead of an error.</summary>
[Collection(PlatformWebCollection.Name)]
public sealed class UnreachableIdentityTests(UnreachableIdentityPlatformFactory site) : IClassFixture<UnreachableIdentityPlatformFactory>
{
    [DbFact]
    public async Task The_sign_in_page_shows_the_local_form_with_a_message()
    {
        HttpResponseMessage response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}

/// <summary>The steps of the local sign-in and of a posted form, as a browser does them.</summary>
internal static partial class SignIn
{
    public static async Task LocalAsync(HttpClient browser, string loginPage)
    {
        HttpResponseMessage signedIn = await PostAsync(browser, loginPage, loginPage, new Dictionary<string, string>
        {
            ["Input.LoginName"] = PlatformFactory.AdminUser,
            ["Input.Password"] = PlatformFactory.AdminPassword,
            ["Input.Remember"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
    }

    /// <summary>Takes the anti-forgery token from <paramref name="page"/> and posts <paramref name="fields"/> to <paramref name="target"/>.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string target, Dictionary<string, string>? fields = null)
    {
        string html = await browser.GetStringAsync(page);
        string token = TokenRegex().Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"No anti-forgery token on {page}");
        var form = new Dictionary<string, string>(fields ?? []) { ["__RequestVerificationToken"] = token };
        return await browser.PostAsync(target, new FormUrlEncodedContent(form));
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

using System.Globalization;
using MatMail;
using MatMail.Api;
using MatMail.Backup;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.MailServer.Imap;
using MatMail.MailServer.Smtp;
using MatMail.MailSync;
using MatMail.Messaging;
using MatMail.Push;
using MatMail.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Container health check: `dotnet MatMail.dll --healthcheck`.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

// ---------------------------------------------------------------------------------------------
// Data directory (config, keys, certificates) and configuration
// ---------------------------------------------------------------------------------------------
string dataDir = AppConfigLoader.ResolveDataDir();
foreach (string sub in new[] { "config", "keys", "certs", "tmp" })
{
    Directory.CreateDirectory(Path.Combine(dataDir, sub));
}

AppConfig config = AppConfigLoader.Load(dataDir);
AppInfo.DataDir = dataDir;

using ILoggerFactory bootstrapLogging = LoggerFactory.Create(b => b.AddConsole());

// ---------------------------------------------------------------------------------------------
// Backup and restore come first, while nothing else uses the database and the data volume: the command line (--backup, --restore),
// a restore that the web interface asked for before it stopped, or one from the environment into an empty installation.
// ---------------------------------------------------------------------------------------------
StartupOutcome startup = await RestoreStartup.RunAsync(args, config, dataDir, bootstrapLogging.CreateLogger("Restore"));
if (startup.ExitCode is int exitCode)
{
    return exitCode;
}

if (startup.Restored)
{
    config = AppConfigLoader.Load(dataDir);   // the settings file of the backup (apart from how this server is deployed)
}

var builder = WebApplication.CreateBuilder(args);
var certificates = new CertificateProvider(config, dataDir, bootstrapLogging.CreateLogger("Certificates"));

// ---------------------------------------------------------------------------------------------
// Web server (Kestrel): one port for the web interface, HTTPS unless a proxy terminates TLS
// ---------------------------------------------------------------------------------------------
builder.WebHost.ConfigureMatMailWebServer(config, certificates);

// ---------------------------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton(certificates);
builder.Services.AddMatMailServices(config);
builder.Services.AddMailSync();
builder.Services.AddImapServer();
builder.Services.AddSmtpServer();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<PushNotifier>();
builder.Services.AddHostedService<BackupScheduler>();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("MatMail");

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // like the session cookie: Secure whenever the page came over HTTPS
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "MatMail.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = SignInService.SessionLifetime;
        options.EventsType = typeof(SessionCookieEvents);
    });

// API calls answer 401/403 instead of redirecting to the login page.
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    var originalLogin = options.Events.OnRedirectToLogin;
    var originalDenied = options.Events.OnRedirectToAccessDenied;
    options.Events.OnRedirectToLogin = context => IsApi(context.Request) ? Respond(context.Response, 401) : originalLogin(context);
    options.Events.OnRedirectToAccessDenied = context => IsApi(context.Request) ? Respond(context.Response, 403) : originalDenied(context);
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Permissions.SystemAdminPolicy, policy => policy.RequireClaim(AppClaims.SystemAdmin, "1"));
    foreach (string permission in Permissions.All)
    {
        options.AddPolicy(permission, policy => policy.RequireAssertion(context =>
            context.User.HasClaim(AppClaims.SystemAdmin, "1") || context.User.HasClaim(AppClaims.Permission, permission)));
    }

    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Localization: the resource keys are the English source strings, so untranslated text still reads in English.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AllowAnonymousToPage("/Error");
        options.Conventions.AllowAnonymousToPage("/Account/Login");
        options.Conventions.AllowAnonymousToPage("/Account/TwoFactor");
        options.Conventions.AllowAnonymousToPage("/Account/Logout");
        options.Conventions.AllowAnonymousToPage("/Account/Setup");
        options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
        options.Conventions.AllowAnonymousToPage("/Account/Language");

        // One line per admin area: who may open it.
        options.Conventions.AuthorizeFolder("/Admin/Tenants", Permissions.SystemAdminPolicy);
        options.Conventions.AuthorizeFolder("/Admin/Settings", Permissions.SystemAdminPolicy);
        options.Conventions.AuthorizeFolder("/Admin/Backup", Permissions.SystemAdminPolicy);
        options.Conventions.AuthorizeFolder("/Admin/Users", Permissions.UsersManage);
        options.Conventions.AuthorizeFolder("/Admin/Roles", Permissions.RolesManage);
        options.Conventions.AuthorizeFolder("/Admin/Domains", Permissions.DomainsManage);
        options.Conventions.AuthorizeFolder("/Admin/Mailboxes", Permissions.MailboxesManage);
        options.Conventions.AuthorizeFolder("/Admin/Accounts", Permissions.AccountsManage);
        options.Conventions.AuthorizeFolder("/Admin/Signatures", Permissions.SignaturesManage);
        options.Conventions.AuthorizeFolder("/Admin/Templates", Permissions.SignaturesManage);
        options.Conventions.AuthorizeFolder("/Admin/Relay", Permissions.RelayManage);
        options.Conventions.AuthorizeFolder("/Admin/Queue", Permissions.QueueManage);
        options.Conventions.AuthorizeFolder("/Admin/Logs", Permissions.LogsView);
        options.Conventions.AuthorizeFolder("/Admin/Unassigned", Permissions.UnassignedManage);
        options.Conventions.AuthorizeFolder("/Admin/Branding", Permissions.BrandingManage);
        options.Conventions.AuthorizeFolder("/Admin/Security", Permissions.SecurityManage);
        options.Conventions.AuthorizePage("/Mail/Index", Permissions.MailUse);
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options => options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

// Non-nullable reference types would get an implicit [Required] with a framework message that never reaches our resources;
// the page models do their own (localized) checks.
builder.Services.Configure<MvcOptions>(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var languages = new[] { new CultureInfo("en-US"), new CultureInfo("de-DE") };
    CultureInfo configured = languages.FirstOrDefault(c => string.Equals(c.Name, config.Display.Culture, StringComparison.OrdinalIgnoreCase)) ?? languages[0];

    // The neutral parents ("de", "en") are listed too: ASP.NET falls back from de-AT to de, but never from "de" to "de-DE".
    var supported = languages.Concat(languages.Select(c => c.Parent)).ToList();
    options.DefaultRequestCulture = new RequestCulture(configured);
    options.SupportedCultures = supported;
    options.SupportedUICultures = supported;

    // The user's saved language wins, then the cookie of the language switch, then the browser.
    options.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
    options.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(context =>
    {
        string? culture = context.User.FindFirst(AppClaims.Culture)?.Value;
        return Task.FromResult(string.IsNullOrEmpty(culture) ? null : new ProviderCultureResult(culture));
    }));
});

if (config.Server.TrustProxyHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// ---------------------------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------------------------
WebApplication app = builder.Build();
ILogger startupLogger = app.Logger;
startupLogger.LogInformation("MatMail {Version} starting. Data directory: {DataDir}", AppInfo.Version, dataDir);

try
{
    await StartupInitializer.MigrateDatabaseAsync(app.Services, startupLogger);
}
catch (MatMail.Versioning.IncompatibleVersionException ex)
{
    // A database that a newer version upgraded: say so plainly instead of a stack trace, and do not start.
    startupLogger.LogCritical("{Message}", ex.Message);
    return 2;
}

await StartupInitializer.SeedAdministratorFromEnvironmentAsync(app.Services, startupLogger);

if (config.Server.TrustProxyHeaders)
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

// Errors get a readable page; re-execute keeps the original status code.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
app.UseMiddleware<SetupRedirectMiddleware>();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseMiddleware<TwoFactorSetupMiddleware>();
app.UseAuthorization();

app.MapGet("/healthz", async (MatMailDbContext db, CancellationToken cancel) =>
{
    bool database = await db.Database.CanConnectAsync(cancel);
    return database
        ? Results.Ok(new { status = "ok", version = AppInfo.Version })
        : Results.Json(new { status = "database unavailable", version = AppInfo.Version }, statusCode: 503);
}).AllowAnonymous();

// The logo of a tenant (part of its sign-in page, so no sign-in needed). The address holds a token that changes with every upload.
app.MapGet("/brand/{token:guid}", async (Guid token, HttpContext http, BrandingService branding, CancellationToken cancel) =>
{
    (byte[] Bytes, string ContentType)? logo = await branding.FindLogoAsync(token, cancel);
    if (logo is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    return Results.Bytes(logo.Value.Bytes, logo.Value.ContentType);
}).AllowAnonymous();

// The web app manifest. The link in the layouts asks with the cookies, so a signed-in person gets the name of their tenant and the
// colours of their theme; without a session it is the plain MatMail one.
app.MapGet("/manifest.webmanifest", async (HttpContext http, BrandingService branding, CurrentUser user, ThemeService themes, Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l, CancellationToken cancel) =>
{
    Brand brand = await branding.GetAsync(user.TenantId, cancel);
    string json = System.Text.Json.JsonSerializer.Serialize(PwaManifest.Build(brand.Name ?? "MatMail", l["Compose"].Value, themes.Resolve().Mode == "dark"));
    http.Response.Headers.CacheControl = "no-cache";
    return Results.Text(json, "application/manifest+json");
}).AllowAnonymous();

// A tenant's own sign-in address: /t/<name>.
app.MapGet("/t/{slug}", (string slug) => Results.Redirect("/Account/Login?t=" + Uri.EscapeDataString(slug))).AllowAnonymous();

app.MapRazorPages();
app.MapMailApi();
await app.RunAsync();
return 0;

static bool IsApi(HttpRequest request)
    => request.Path.StartsWithSegments("/api") || string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

static Task Respond(HttpResponse response, int status)
{
    response.StatusCode = status;
    return Task.CompletedTask;
}

// Makes the Program class visible to the integration tests.
public partial class Program
{
}

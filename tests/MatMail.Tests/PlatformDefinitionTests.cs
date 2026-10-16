using MatMail.Platform;
using MatMail.Services;
using MatPlatform;
using Microsoft.AspNetCore.Http;

namespace MatMail.Tests;

/// <summary>
/// The description of MatMail in the Mat platform (<see cref="MailPlatform.Describe"/>), without a database: valid for MatPlatform's validator in
/// both languages and against MatMail's copy of the MatUi sprite, and what Mattiverse and MatIdentity rely on (id, accent, roles, few settings).
/// </summary>
public sealed class PlatformDefinitionTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MailPlatform.Describe, language), Sprite()));

    [Fact]
    public void It_is_matmail_with_its_accent_and_the_icon_exists()
    {
        MatApiDefinition definition = MatPlatformBuilder.Build(MailPlatform.Describe, "en");

        Assert.Equal("matmail", definition.App.Id);
        Assert.Equal("MatMail", definition.App.Name);
        Assert.Equal("#475569", definition.App.Accent);
        Assert.Equal("/", definition.App.Home);
        Assert.True(File.Exists(Path.Combine(Root(), "src", "MatMail", "wwwroot", definition.App.Icon!.TrimStart('/'))));
    }

    [Fact]
    public void The_pages_are_overview_and_settings_and_the_settings_are_few()
    {
        MatApiDefinition definition = MatPlatformBuilder.Build(MailPlatform.Describe, "de");

        Assert.Equal(["overview", "settings"], definition.Pages.Select(p => p.Id));
        Assert.InRange(definition.Configure.Count, 1, 10);
        Assert.DoesNotContain(definition.Configure, f => f.Type == "secret");

        // Everything is in app.json, which the server reads at its start.
        Assert.All(definition.Configure, f => Assert.True(f.Restart, $"{f.Key} applies after a restart"));
        Assert.Equal(["en-US", "de-DE"], definition.Configure.Single(f => f.Key == "culture").Options!.Select(o => o.Value));
    }

    [Fact]
    public void The_roles_are_those_of_the_installation_most_powerful_first()
    {
        MatApiDefinition definition = MatPlatformBuilder.Build(MailPlatform.Describe, "en");

        Assert.Equal(PlatformRoles.All, definition.Roles!.Select(r => r.Key));
        Assert.Equal(["System administrator", "Tenant administrator", "User"], definition.Roles!.Select(r => r.Label));
    }

    [Fact]
    public void The_actions_are_links_into_the_admin_area_and_two_that_run()
    {
        MatApiDefinition definition = MatPlatformBuilder.Build(MailPlatform.Describe, "en");

        Assert.Equal(["fetchMail", "retryQueue"], definition.Actions.Where(a => a.Kind == "post").Select(a => a.Id));
        Assert.All(definition.Actions.Where(a => a.Kind == "link"), a => Assert.StartsWith("/Admin", a.Url));
        Assert.False(string.IsNullOrEmpty(definition.Actions.Single(a => a.Id == "retryQueue").Confirm));
    }

    [Fact]
    public void Both_languages_describe_the_same_keys()
    {
        MatApiDefinition de = MatPlatformBuilder.Build(MailPlatform.Describe, "de");
        MatApiDefinition en = MatPlatformBuilder.Build(MailPlatform.Describe, "en");

        Assert.Equal(de.State.Select(s => s.Key), en.State.Select(s => s.Key));
        Assert.Equal(de.Configure.Select(f => f.Key), en.Configure.Select(f => f.Key));
        Assert.Equal("Übersicht", de.Pages[0].Label);
        Assert.Equal("Overview", en.Pages[0].Label);
    }

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/health", true)]
    [InlineData("/API/Health", true)]
    [InlineData("/api/state", true)]
    [InlineData("/api/state/users", true)]
    [InlineData("/api/configure", true)]
    [InlineData("/api/actions/fetchMail", true)]
    [InlineData("/api/mail/bootstrap", false)]
    [InlineData("/api/healthy", false)]
    [InlineData("/Admin", false)]
    public void The_setup_redirect_leaves_only_the_platform_alone(string path, bool platform)
        => Assert.Equal(platform, SetupRedirectMiddleware.IsPlatform(new PathString(path)));

    private static IReadOnlySet<string> Sprite()
        => MatPlatformValidator.ReadSprite(Path.Combine(Root(), "src", "MatMail", "wwwroot", "matui", "matui-icons.svg"));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}

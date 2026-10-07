using MatMail.Configuration;
using MatMail.Controls;
using MatMail.Services;

namespace MatMail.Tests;

public class MailAddressTests
{
    [Theory]
    [InlineData("max.mueller@example.com", true)]
    [InlineData("  Max.Mueller@Example.COM ", true)]
    [InlineData("mailto:info@example.org", true)]
    [InlineData("*@example.com", true)]
    [InlineData("a@b", false)]
    [InlineData("no-at-sign", false)]
    [InlineData("@example.com", false)]
    [InlineData("max@", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("a..b@example.com", false)]
    [InlineData("max@-example.com", false)]
    public void IsValid_recognises_addresses(string address, bool expected)
        => Assert.Equal(expected, MailAddresses.IsValid(address));

    [Fact]
    public void Normalize_lowercases_and_strips_decoration()
        => Assert.Equal("max@example.com", MailAddresses.Normalize(" <Max@Example.com> "));

    [Fact]
    public void Catch_all_addresses_are_recognised()
    {
        Assert.True(MailAddresses.IsCatchAll("*@example.com"));
        Assert.False(MailAddresses.IsCatchAll("info@example.com"));
        Assert.Equal("*@example.com", MailAddresses.CatchAllOf("Example.com"));
        Assert.Equal("example.com", MailAddresses.DomainOf("a@example.com"));
    }
}

public class PermissionTests
{
    [Fact]
    public void Every_permission_has_a_description_and_a_unique_key()
    {
        Assert.Equal(Permissions.Catalogue.Count, Permissions.All.Distinct().Count());
        Assert.All(Permissions.Catalogue, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Plain_users_only_get_mail_access()
        => Assert.Equal(new[] { Permissions.MailUse }, Permissions.UserDefaults);

    [Fact]
    public void System_users_hold_every_permission()
    {
        var user = new CurrentUser();
        user.RunAsSystem();
        Assert.All(Permissions.All, p => Assert.True(user.Can(p)));
    }

    [Fact]
    public void A_delegated_actor_only_holds_what_it_was_given()
    {
        var user = new CurrentUser();
        user.RunAs(7, 3, isSystemAdmin: false, new[] { Permissions.MailUse });
        Assert.True(user.Can(Permissions.MailUse));
        Assert.False(user.Can(Permissions.UsersManage));
        Assert.Equal(3, user.TenantId);
        Assert.False(user.CanAdminister);
    }
}

public class PasswordPolicyTests
{
    [Fact]
    public void The_message_names_the_minimum_length()
        => Assert.Contains(SignInService.MinPasswordLength.ToString(), SignInService.PasswordTooShortMessage);

    [Theory]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("exactly-10", true)]
    [InlineData("a-much-longer-password", true)]
    public void Short_passwords_are_refused(string password, bool accepted)
        => Assert.Equal(accepted, SignInService.ValidatePasswordStrength(password) is null);
}

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmail-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_missing_file_is_created_with_defaults()
    {
        AppConfig config = AppConfigLoader.Load(_dir);
        Assert.Equal(9933, config.Server.WebPort);
        Assert.True(File.Exists(AppConfigLoader.ConfigPath(_dir)));
    }

    [Fact]
    public void Environment_variables_override_the_file()
    {
        Environment.SetEnvironmentVariable("MATMAIL__Server__Hostname", "mail.test.example");
        try
        {
            AppConfig config = AppConfigLoader.Load(_dir);
            Assert.Equal("mail.test.example", config.Server.Hostname);

            // The override is not written back into the file.
            Assert.Equal("localhost", AppConfigLoader.LoadFile(_dir).Server.Hostname);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MATMAIL__Server__Hostname", null);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}

public class IconTests
{
    [Theory]
    [InlineData("mail")]
    [InlineData("inbox")]
    [InlineData("trash")]
    [InlineData("star")]
    public void Common_icons_exist(string name) => Assert.True(Icons.Exists(name));

    [Fact]
    public void The_sprite_contains_every_icon_once()
    {
        string sprite = Icons.Sprite();
        Assert.Contains("id=\"i-mail\"", sprite);
        Assert.Equal(sprite.IndexOf("id=\"i-mail\"", StringComparison.Ordinal), sprite.LastIndexOf("id=\"i-mail\"", StringComparison.Ordinal));
    }
}

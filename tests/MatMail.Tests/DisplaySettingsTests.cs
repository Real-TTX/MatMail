using System.Security.Claims;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

public class DisplaySettingsTests
{
    private static HttpContextAccessor Signed(params (string Type, string Value)[] claims)
        => new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test")) } };

    private static AppConfig Config()
    {
        var config = new AppConfig();
        config.Display.TimeZone = "Europe/Berlin";
        return config;
    }

    [Fact]
    public void Dates_are_shown_in_the_time_zone_of_the_server_unless_the_user_chose_one()
    {
        var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(13, new Fmt(Config(), Signed()).ToLocal(winter).Hour);
        Assert.Equal(21, new Fmt(Config(), Signed((AppClaims.TimeZone, "Asia/Tokyo"))).ToLocal(winter).Hour);
        Assert.Equal(13, new Fmt(Config(), Signed((AppClaims.TimeZone, "Nowhere/Land"))).ToLocal(winter).Hour);
        Assert.Equal(13, new Fmt(Config(), new HttpContextAccessor()).ToLocal(winter).Hour);
    }

    [Fact]
    public void Time_zones_are_checked_by_the_server_that_has_to_use_them()
    {
        Assert.True(Fmt.IsKnownZone("Europe/Berlin"));
        Assert.False(Fmt.IsKnownZone("Europe/Atlantis"));
        Assert.False(Fmt.IsKnownZone(""));
        Assert.False(Fmt.IsKnownZone(null));
    }

    [Fact]
    public void Without_a_choice_the_look_is_the_standard_one()
    {
        ThemeChoice theme = new ThemeService(Signed(), Config()).Resolve();

        Assert.Equal("normal", theme.TextSize);
        Assert.Equal("comfortable", theme.Density);
        Assert.True(theme.ShowPreviews);
        Assert.Null(theme.UserTimeZone);
        Assert.Equal("off", theme.ReadingPane);
        Assert.False(theme.ConversationView);
    }

    [Fact]
    public void The_choices_of_the_user_are_used()
    {
        ThemeChoice theme = new ThemeService(Signed(
            (AppClaims.TextSize, "large"), (AppClaims.Density, "compact"), (AppClaims.TimeZone, "Asia/Tokyo"), (AppClaims.ShowPreviews, "0"),
            (AppClaims.ReadingPane, "below"), (AppClaims.ConversationView, "1")), Config()).Resolve();

        Assert.Equal("large", theme.TextSize);
        Assert.Equal("compact", theme.Density);
        Assert.False(theme.ShowPreviews);
        Assert.Equal("Asia/Tokyo", theme.UserTimeZone);
        Assert.Equal("below", theme.ReadingPane);
        Assert.True(theme.ConversationView);
    }

    [Fact]
    public void Values_nobody_offers_fall_back_to_the_standard()
    {
        ThemeChoice theme = new ThemeService(Signed(
            (AppClaims.TextSize, "gigantic"), (AppClaims.Density, "cramped"), (AppClaims.TimeZone, "Mars/Olympus"), (AppClaims.ReadingPane, "diagonal")), Config()).Resolve();

        Assert.Equal("normal", theme.TextSize);
        Assert.Equal("comfortable", theme.Density);
        Assert.Null(theme.UserTimeZone);
        Assert.Equal("off", theme.ReadingPane);
    }

    [Fact]
    public void The_settings_travel_in_the_claims_of_the_session()
    {
        var snapshot = new SessionSnapshot(
            1, 1, "Home", 1, "alice", "Alice", false, false, new[] { Permissions.MailUse }, null, null, null,
            "small", "compact", "Asia/Tokyo", false, DateTime.UtcNow.AddDays(1), false, false, ReadingPane: "right", ConversationView: true);

        ClaimsPrincipal principal = SignInService.BuildPrincipal(snapshot, Guid.NewGuid());

        Assert.Equal("small", principal.FindFirstValue(AppClaims.TextSize));
        Assert.Equal("compact", principal.FindFirstValue(AppClaims.Density));
        Assert.Equal("Asia/Tokyo", principal.FindFirstValue(AppClaims.TimeZone));
        Assert.Equal("0", principal.FindFirstValue(AppClaims.ShowPreviews));
        Assert.Equal("right", principal.FindFirstValue(AppClaims.ReadingPane));
        Assert.Equal("1", principal.FindFirstValue(AppClaims.ConversationView));

        ThemeChoice theme = new ThemeService(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, Config()).Resolve();
        Assert.Equal("small", theme.TextSize);
        Assert.False(theme.ShowPreviews);
        Assert.Equal("right", theme.ReadingPane);
        Assert.True(theme.ConversationView);
    }
}

public class DisplaySettingsStorageTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Seed _seed = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<SessionSnapshot> SnapshotOfAsync(User user)
    {
        var token = Guid.NewGuid();
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.UserSessions.Add(new UserSession
        {
            Token = token, UserId = user.Id, TenantId = user.TenantId, ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (await scope.ServiceProvider.GetRequiredService<SignInService>().LoadSnapshotAsync(token, touch: false))!;
    }

    [DbFact]
    public async Task A_new_user_sees_previews_and_has_no_other_preferences()
    {
        SessionSnapshot snapshot = await SnapshotOfAsync(_seed.Alice);

        Assert.True(snapshot.ShowPreviews);
        Assert.Null(snapshot.TextSize);
        Assert.Null(snapshot.Density);
        Assert.Null(snapshot.TimeZone);
        Assert.Null(snapshot.ReadingPane);
        Assert.False(snapshot.ConversationView);
    }

    [DbFact]
    public async Task The_settings_of_the_user_reach_the_session()
    {
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            User alice = await db.Users.FirstAsync(u => u.Id == _seed.Alice.Id);
            alice.TextSize = "large";
            alice.Density = "compact";
            alice.TimeZone = "America/New_York";
            alice.ShowPreviews = false;
            alice.ReadingPane = "right";
            alice.ConversationView = true;
            await db.SaveChangesAsync();
        }

        SessionSnapshot snapshot = await SnapshotOfAsync(_seed.Alice);

        Assert.Equal("large", snapshot.TextSize);
        Assert.Equal("compact", snapshot.Density);
        Assert.Equal("America/New_York", snapshot.TimeZone);
        Assert.False(snapshot.ShowPreviews);
        Assert.Equal("right", snapshot.ReadingPane);
        Assert.True(snapshot.ConversationView);
    }
}

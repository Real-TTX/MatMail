using MatMail.Data;
using MatMail.Services;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatMail.Tests;

public class BrandingColorTests
{
    [Theory]
    [InlineData("#1A73E8", "#1a73e8")]
    [InlineData(" #fff ", "#ffffff")]
    [InlineData("#0af", "#00aaff")]
    [InlineData("1a73e8", null)]
    [InlineData("#12345", null)]
    [InlineData("red", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Colours_are_normalised_to_six_digits_or_refused(string? input, string? expected)
        => Assert.Equal(expected, BrandingService.NormalizeColor(input));

    [Theory]
    [InlineData("#1a73e8", "#ffffff")]
    [InlineData("#0f766e", "#ffffff")]
    [InlineData("#fbbf24", "#1b2130")]
    [InlineData("#ffffff", "#1b2130")]
    [InlineData("#000000", "#ffffff")]
    public void The_text_on_a_colour_is_the_one_that_reads_better(string background, string expected)
        => Assert.Equal(expected, BrandingService.TextOn(background));

    [Fact]
    public void Dark_mode_gets_a_lighter_version_of_the_colour()
    {
        BrandPalette palette = BrandingService.Palette("#1a73e8");
        Assert.Equal("#1a73e8", palette.Light);
        Assert.Matches("^#[0-9a-f]{6}$", palette.Dark);
        Assert.NotEqual(palette.Light, palette.Dark);
        Assert.True(Convert.ToInt32(palette.Dark[1..3], 16) > 0x1a);
    }

    [Fact]
    public void Pictures_are_recognised_by_their_bytes_not_by_a_name()
    {
        Assert.Equal("image/png", BrandingService.DetectImageType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal("image/jpeg", BrandingService.DetectImageType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal("image/gif", BrandingService.DetectImageType("GIF89a"u8.ToArray()));
        Assert.Equal("image/webp", BrandingService.DetectImageType("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Equal("image/svg+xml", BrandingService.DetectImageType("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()));
        Assert.Equal("image/svg+xml", BrandingService.DetectImageType("  <svg/>"u8.ToArray()));
        Assert.Null(BrandingService.DetectImageType("<html><script>alert(1)</script></html>"u8.ToArray()));
        Assert.Null(BrandingService.DetectImageType("MZ\u0090\0"u8.ToArray()));
        Assert.Null(BrandingService.DetectImageType(Array.Empty<byte>()));
    }
}

public class BrandingStorageTests : IAsyncLifetime
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private TestHost _host = null!;
    private Seed _seed = null!;
    private BrandingService _branding = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _branding = _host.Services.GetRequiredService<BrandingService>();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DbFact]
    public async Task A_tenant_without_branding_looks_like_matmail()
    {
        Brand brand = await _branding.GetAsync(_seed.Tenant.Id);
        Assert.Null(brand.Name);
        Assert.False(brand.HasLogo);
        Assert.Null(brand.AccentColor);
        Assert.Equal(Brand.None, await _branding.GetAsync(null));
    }

    [DbFact]
    public async Task Branding_is_saved_found_by_its_sign_in_name_and_the_logo_by_its_token()
    {
        string? error = await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput
        {
            BrandName = "Example Mail", Slug = "Example-Mail", Website = "https://example.test", AccentColor = "#0F766E", NewLogo = Png,
        });
        Assert.Null(error);

        Brand brand = await _branding.GetAsync(_seed.Tenant.Id);
        Assert.Equal("Example Mail", brand.Name);
        Assert.Equal("example-mail", brand.Slug);
        Assert.Equal("#0f766e", brand.AccentColor);
        Assert.Equal("https://example.test", brand.Website);
        Assert.NotNull(brand.LogoToken);
        Assert.StartsWith("/brand/", brand.LogoUrl);

        Brand? bySlug = await _branding.FindBySlugAsync(" EXAMPLE-mail ");
        Assert.Equal("Example Mail", bySlug?.Name);
        Assert.Null(await _branding.FindBySlugAsync("nobody-here"));
        Assert.Null(await _branding.FindBySlugAsync("no spaces allowed"));

        (byte[] Bytes, string ContentType)? logo = await _branding.FindLogoAsync(brand.LogoToken!.Value);
        Assert.Equal("image/png", logo?.ContentType);
        Assert.Equal(Png, logo?.Bytes);
        Assert.Null(await _branding.FindLogoAsync(Guid.NewGuid()));
    }

    [DbFact]
    public async Task A_new_logo_gets_a_new_address_and_the_logo_can_be_removed()
    {
        await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { NewLogo = Png });
        Guid first = (await _branding.GetAsync(_seed.Tenant.Id)).LogoToken!.Value;

        await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { NewLogo = Png.Concat(new byte[] { 1 }).ToArray() });
        Guid second = (await _branding.GetAsync(_seed.Tenant.Id)).LogoToken!.Value;
        Assert.NotEqual(first, second);
        Assert.Null(await _branding.FindLogoAsync(first));

        // Saving without a file keeps the logo; asking for removal drops it.
        await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { BrandName = "Changed" });
        Assert.Equal(second, (await _branding.GetAsync(_seed.Tenant.Id)).LogoToken);

        await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { BrandName = "Changed", RemoveLogo = true });
        Brand removed = await _branding.GetAsync(_seed.Tenant.Id);
        Assert.False(removed.HasLogo);
        Assert.Null(await _branding.FindLogoAsync(second));
    }

    [DbFact]
    public async Task Wrong_input_is_refused_and_nothing_is_saved()
    {
        Assert.Equal("The colour must look like #1a73e8.", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { AccentColor = "blue" }));
        Assert.Equal("The web address must start with http:// or https://.", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Website = "javascript:alert(1)" }));
        Assert.StartsWith("The sign-in name may only contain", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Slug = "Not OK!" }));
        Assert.StartsWith("The sign-in name may only contain", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Slug = "ab" }));
        Assert.Equal("The logo must be a PNG, JPEG, GIF, WebP or SVG picture.", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { NewLogo = "<script>x</script>"u8.ToArray() }));
        Assert.Equal("The logo is larger than 512 KB.", await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { NewLogo = Png.Concat(new byte[BrandingService.MaxLogoBytes]).ToArray() }));
        Assert.Equal("The tenant does not exist.", await _branding.SaveAsync(long.MaxValue, new BrandingInput()));

        Assert.Equal(Brand.None with { Slug = null }, await _branding.GetAsync(_seed.Tenant.Id));
    }

    [DbFact]
    public async Task A_sign_in_name_belongs_to_one_tenant_only()
    {
        Tenant other;
        using (IServiceScope scope = _host.Scope())
        {
            (Tenant? created, string? error) = await scope.ServiceProvider.GetRequiredService<TenantService>().CreateAsync("Other", null);
            Assert.Null(error);
            other = created!;
        }

        Assert.Null(await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Slug = "shared-name" }));
        Assert.Equal("This sign-in name is already taken.", await _branding.SaveAsync(other.Id, new BrandingInput { Slug = "shared-name" }));
        Assert.Null(await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Slug = "shared-name", BrandName = "Same tenant again" }));
    }

    [DbFact]
    public async Task An_inactive_tenant_has_no_sign_in_page()
    {
        await _branding.SaveAsync(_seed.Tenant.Id, new BrandingInput { Slug = "paused" });
        using (IServiceScope scope = _host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
            await db.Tenants.Where(t => t.Id == _seed.Tenant.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false));
        }

        Assert.Null(await _branding.FindBySlugAsync("paused"));
    }
}

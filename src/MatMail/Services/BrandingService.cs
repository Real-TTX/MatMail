using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>How a tenant presents itself. Everything is optional: without a brand the application looks like MatMail.</summary>
public sealed record Brand(string? Name, string? Website, string? AccentColor, Guid? LogoToken, string? Slug)
{
    public static readonly Brand None = new(null, null, null, null, null);

    public bool HasLogo => LogoToken is not null;

    /// <summary>The address of the logo: unguessable (it contains the token) and different after every upload, so it can be cached for good.</summary>
    public string? LogoUrl => LogoToken is Guid token ? $"/brand/{token:N}" : null;
}

/// <summary>What the branding form sends.</summary>
public sealed class BrandingInput
{
    public string? Slug { get; set; }
    public string? BrandName { get; set; }
    public string? Website { get; set; }
    public string? AccentColor { get; set; }

    /// <summary>A new logo (the bytes of the uploaded file), or null to keep the current one.</summary>
    public byte[]? NewLogo { get; set; }

    public bool RemoveLogo { get; set; }
}

/// <summary>Colours derived from the one colour a tenant picked: for light mode, for dark mode, and the text colour on top of each.</summary>
public sealed record BrandPalette(string Light, string Dark, string OnLight, string OnDark);

/// <summary>
/// Reads and saves the branding of tenants (name, logo, accent colour, own sign-in address). The layouts ask on every page, so
/// the answers are cached for a short time and forgotten when something is saved.
/// </summary>
public sealed partial class BrandingService
{
    public const int MaxLogoBytes = 512 * 1024;

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentDictionary<long, (Brand Brand, DateTime LoadedAt)> _cache = new();

    public BrandingService(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>The brand of a tenant (<see cref="Brand.None"/> without tenant or branding).</summary>
    public async Task<Brand> GetAsync(long? tenantId, CancellationToken cancel = default)
    {
        if (tenantId is not long id)
        {
            return Brand.None;
        }

        if (_cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.LoadedAt < Lifetime)
        {
            return cached.Brand;
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        string? slug = await db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => t.Slug).FirstOrDefaultAsync(cancel);
        var row = await db.TenantBrandings.IgnoreQueryFilters().AsNoTracking().Where(b => b.TenantId == id)
            .Select(b => new { b.BrandName, b.Website, b.AccentColor, b.LogoToken }).FirstOrDefaultAsync(cancel);
        Brand brand = new(row?.BrandName, row?.Website, row?.AccentColor, row?.LogoToken, slug);
        _cache[id] = (brand, DateTime.UtcNow);
        return brand;
    }

    /// <summary>The brand behind a tenant's own sign-in address (/t/slug), or null when there is no such tenant.</summary>
    public async Task<Brand?> FindBySlugAsync(string? slug, CancellationToken cancel = default)
    {
        string name = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(name))
        {
            return null;
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        long? tenantId = await db.Tenants.AsNoTracking().Where(t => t.Slug == name && t.IsActive).Select(t => (long?)t.Id).FirstOrDefaultAsync(cancel);
        return tenantId is null ? null : await GetAsync(tenantId, cancel);
    }

    /// <summary>The logo with this token (anyone with the address may fetch it: it is part of the sign-in page).</summary>
    public async Task<(byte[] Bytes, string ContentType)?> FindLogoAsync(Guid token, CancellationToken cancel = default)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var row = await db.TenantBrandings.IgnoreQueryFilters().AsNoTracking().Where(b => b.LogoToken == token && b.Logo != null)
            .Select(b => new { b.Logo, b.LogoContentType }).FirstOrDefaultAsync(cancel);
        return row?.Logo is null ? null : (row.Logo, row.LogoContentType ?? "application/octet-stream");
    }

    /// <summary>Saves the branding of a tenant. Returns an English error text, or null when it worked.</summary>
    public async Task<string?> SaveAsync(long tenantId, BrandingInput input, CancellationToken cancel = default)
    {
        string? slug = string.IsNullOrWhiteSpace(input.Slug) ? null : input.Slug.Trim().ToLowerInvariant();
        if (slug is not null && !SlugPattern().IsMatch(slug))
        {
            return "The sign-in name may only contain lower-case letters, digits and hyphens (3 to 40 characters).";
        }

        string? color = NormalizeColor(input.AccentColor);
        if (!string.IsNullOrWhiteSpace(input.AccentColor) && color is null)
        {
            return "The colour must look like #1a73e8.";
        }

        string? website = Clean(input.Website);
        if (website is not null && !IsWebAddress(website))
        {
            return "The web address must start with http:// or https://.";
        }

        string? logoType = null;
        if (input.NewLogo is { Length: > 0 } logo)
        {
            if (logo.Length > MaxLogoBytes)
            {
                return "The logo is larger than 512 KB.";
            }

            logoType = DetectImageType(logo);
            if (logoType is null)
            {
                return "The logo must be a PNG, JPEG, GIF, WebP or SVG picture.";
            }
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        Tenant? tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancel);
        if (tenant is null)
        {
            return "The tenant does not exist.";
        }

        if (slug is not null && await db.Tenants.AnyAsync(t => t.Slug == slug && t.Id != tenantId, cancel))
        {
            return "This sign-in name is already taken.";
        }

        tenant.Slug = slug;
        TenantBranding? branding = await db.TenantBrandings.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.TenantId == tenantId, cancel);
        if (branding is null)
        {
            branding = new TenantBranding { TenantId = tenantId };
            db.TenantBrandings.Add(branding);
        }

        branding.BrandName = Clean(input.BrandName);
        branding.Website = website;
        branding.AccentColor = color;
        if (logoType is not null)
        {
            branding.Logo = input.NewLogo;
            branding.LogoContentType = logoType;
            branding.LogoToken = Guid.NewGuid();
        }
        else if (input.RemoveLogo)
        {
            branding.Logo = null;
            branding.LogoContentType = null;
            branding.LogoToken = null;
        }

        await db.SaveChangesAsync(cancel);
        _cache.TryRemove(tenantId, out _);
        return null;
    }

    /// <summary>"#rrggbb" in lower case for "#RRGGBB" or "#rgb"; null for anything else (including empty).</summary>
    public static string? NormalizeColor(string? text)
    {
        string value = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (Regex.IsMatch(value, "^#[0-9a-f]{3}$"))
        {
            value = "#" + string.Concat(value.Skip(1).Select(c => new string(c, 2)));
        }

        return Regex.IsMatch(value, "^#[0-9a-f]{6}$") ? value : null;
    }

    /// <summary>The colours for light and dark mode and the text on them. The dark mode colour is a lighter version of the same hue.</summary>
    public static BrandPalette Palette(string hex)
    {
        (double r, double g, double b) = Parse(hex);
        string dark = Format(Lighten(r), Lighten(g), Lighten(b));
        return new BrandPalette(hex, dark, TextOn(hex), TextOn(dark));
    }

    /// <summary>White or near-black, whichever reads better on the colour (WCAG contrast).</summary>
    public static string TextOn(string hex)
    {
        (double r, double g, double b) = Parse(hex);
        double luminance = (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
        double againstWhite = 1.05 / (luminance + 0.05);
        double againstDark = (luminance + 0.05) / (0.0132 + 0.05);
        return againstWhite >= againstDark ? "#ffffff" : "#1b2130";
    }

    /// <summary>The content type of a picture by its first bytes (never by its name); null when it is none of the allowed kinds.</summary>
    public static string? DetectImageType(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8')
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return "image/webp";
        }

        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            ? "image/svg+xml"
            : null;
    }

    private static bool IsWebAddress(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static (double R, double G, double B) Parse(string hex)
        => (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0,
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);

    /// <summary>A third of the way to white.</summary>
    private static double Lighten(double channel) => channel + ((1 - channel) * 0.35);

    private static string Format(double r, double g, double b)
        => "#" + string.Concat(new[] { r, g, b }.Select(c => ((int)Math.Round(Math.Clamp(c, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture)));

    private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex SlugPattern();
}

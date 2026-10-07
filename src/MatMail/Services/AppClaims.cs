namespace MatMail.Services;

/// <summary>Custom claim types carried by the session cookie principal (refreshed from the database on every request).</summary>
public static class AppClaims
{
    public const string SessionToken = "matmail:session";
    public const string TenantId = "matmail:tenant";
    public const string TenantName = "matmail:tenantname";
    public const string HomeTenantId = "matmail:hometenant";
    public const string DisplayName = "matmail:displayname";
    public const string SystemAdmin = "matmail:sysadmin";
    public const string Permission = "matmail:permission";
    public const string MustChangePassword = "matmail:mustchangepassword";
    public const string ThemeMode = "matmail:thememode";
    public const string ThemeAccent = "matmail:themeaccent";
    public const string Culture = "matmail:culture";
    public const string TextSize = "matmail:textsize";
    public const string Density = "matmail:density";
    public const string TimeZone = "matmail:timezone";
    public const string ShowPreviews = "matmail:showpreviews";
}

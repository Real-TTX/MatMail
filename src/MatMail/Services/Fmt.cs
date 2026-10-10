using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>
/// Formats dates in the time zone of the signed-in user (else the configured one of the server) and sizes for people.
/// Everything is stored in UTC.
/// </summary>
public sealed class Fmt
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    private readonly TimeZoneInfo _serverZone;
    private readonly IHttpContextAccessor _http;

    public Fmt(AppConfig config, IHttpContextAccessor http)
    {
        _http = http;
        _serverZone = Find(config.Display.TimeZone) ?? TimeZoneInfo.Utc;
    }

    /// <summary>The time zone dates are shown in: the signed-in user's own, otherwise the server's.</summary>
    public TimeZoneInfo Zone => Find(_http.HttpContext?.User.FindFirstValue(AppClaims.TimeZone)) ?? _serverZone;

    /// <summary>Whether the id names a time zone this server knows.</summary>
    public static bool IsKnownZone(string? id) => Find(id) is not null;

    /// <summary>
    /// The zone of an id, or null. Ids that cannot be one ("Europe/Berlin", "Etc/GMT+5", "UTC" are the shapes) are not even looked up,
    /// and only zones that exist are remembered: what a person posts must not grow the memory of the server or reach into its files.
    /// </summary>
    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !ZoneId.IsMatch(id) || id.Contains(".."))
        {
            return null;
        }

        if (Zones.TryGetValue(id, out TimeZoneInfo? known))
        {
            return known;
        }

        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            Zones[id] = zone;
            return zone;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ZoneId = new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public DateTime ToLocal(DateTime utc)
    {
        DateTime value = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc;
        return TimeZoneInfo.ConvertTimeFromUtc(value.ToUniversalTime(), Zone);
    }

    public string DateTimeText(DateTime? utc) => utc is null ? "–" : ToLocal(utc.Value).ToString("g", CultureInfo.CurrentCulture);

    public string DateText(DateTime? utc) => utc is null ? "–" : ToLocal(utc.Value).ToString("d", CultureInfo.CurrentCulture);

    /// <summary>"3 min ago"-style text for recent times, the date otherwise.</summary>
    public string Ago(DateTime? utc)
    {
        if (utc is null)
        {
            return "–";
        }

        TimeSpan age = DateTime.UtcNow - utc.Value.ToUniversalTime();
        if (age.TotalSeconds < 45)
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "gerade eben" : "just now";
        }

        bool de = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        if (age.TotalMinutes < 60)
        {
            int minutes = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return de ? $"vor {minutes} Min." : $"{minutes} min ago";
        }

        if (age.TotalHours < 24)
        {
            int hours = (int)Math.Round(age.TotalHours);
            return de ? $"vor {hours} Std." : $"{hours} h ago";
        }

        return DateTimeText(utc);
    }

    public static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    /// <summary>How much of a limit is used, in whole percent: 100 only when it is reached.</summary>
    public static int Percent(long used, long limit) => limit <= 0 ? 0 : (int)Math.Min(100, used * 100 / limit);

    /// <summary>The state that colours a storage bar: "full" when the limit is reached, "high" from 90 %, "warn" from 75 %, otherwise none. The web client does the same (<c>usageLevel</c>).</summary>
    public static string UsageState(long used, long limit)
        => limit > 0 && used >= limit ? "full" : Percent(used, limit) switch { >= 90 => "high", >= 75 => "warn", _ => string.Empty };
}

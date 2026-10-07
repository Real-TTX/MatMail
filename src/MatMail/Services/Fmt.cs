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
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.Ordinal);

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

    private static TimeZoneInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Zones.GetOrAdd(id, key =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        });
    }

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
}

using System.Globalization;
using MatMail.Configuration;

namespace MatMail.Services;

/// <summary>Formats dates in the configured time zone and sizes for people. Everything is stored in UTC.</summary>
public sealed class Fmt
{
    private readonly TimeZoneInfo _zone;

    public Fmt(AppConfig config)
    {
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(config.Display.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _zone = TimeZoneInfo.Utc;
        }
    }

    public DateTime ToLocal(DateTime utc)
    {
        DateTime value = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc;
        return TimeZoneInfo.ConvertTimeFromUtc(value.ToUniversalTime(), _zone);
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

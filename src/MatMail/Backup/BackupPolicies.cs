using MatMail.Data;

namespace MatMail.Backup;

/// <summary>How long backups are kept: a backup stays when any of the rules keeps it.</summary>
public sealed record RetentionPolicy(int KeepLast, int KeepDaily, int KeepWeekly, int KeepMonthly)
{
    public static RetentionPolicy Of(BackupPlan plan) => new(plan.KeepLast, plan.KeepDaily, plan.KeepWeekly, plan.KeepMonthly);
}

/// <summary>Decides which backups of a plan are no longer wanted.</summary>
public static class BackupRetention
{
    /// <summary>
    /// The names to remove. Kept are the newest <see cref="RetentionPolicy.KeepLast"/> (at least one: the newest backup is never removed),
    /// the newest of each of the last days, weeks (Monday to Sunday) and months, counted in the time zone of the installation.
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<(string Name, DateTime CreatedUtc)> backups, RetentionPolicy policy, DateTime nowUtc, TimeZoneInfo zone)
    {
        List<(string Name, DateTime CreatedUtc)> newestFirst = backups
            .OrderByDescending(b => b.CreatedUtc)
            .ThenByDescending(b => b.Name, StringComparer.Ordinal)
            .ToList();

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, _) in newestFirst.Take(Math.Max(1, policy.KeepLast)))
        {
            keep.Add(name);
        }

        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

        DateTime today = Local(nowUtc).Date;
        DateTime thisWeek = WeekStart(today);
        var thisMonth = new DateTime(today.Year, today.Month, 1);

        if (policy.KeepDaily > 0)
        {
            KeepNewestOf(newestFirst, b => Local(b.CreatedUtc).Date, day => day > today.AddDays(-policy.KeepDaily), keep);
        }

        if (policy.KeepWeekly > 0)
        {
            KeepNewestOf(newestFirst, b => WeekStart(Local(b.CreatedUtc)), week => week >= thisWeek.AddDays(-7 * (policy.KeepWeekly - 1)), keep);
        }

        if (policy.KeepMonthly > 0)
        {
            KeepNewestOf(newestFirst, b => new DateTime(Local(b.CreatedUtc).Year, Local(b.CreatedUtc).Month, 1), month => month >= thisMonth.AddMonths(-(policy.KeepMonthly - 1)), keep);
        }

        return newestFirst.Where(b => !keep.Contains(b.Name)).Select(b => b.Name).ToList();
    }

    /// <summary>The first (= newest) backup of every period that is still wanted.</summary>
    private static void KeepNewestOf(
        List<(string Name, DateTime CreatedUtc)> newestFirst,
        Func<(string Name, DateTime CreatedUtc), DateTime> periodOf,
        Func<DateTime, bool> wanted,
        HashSet<string> keep)
    {
        var seen = new HashSet<DateTime>();
        foreach ((string Name, DateTime CreatedUtc) backup in newestFirst)
        {
            DateTime period = periodOf(backup);
            if (wanted(period) && seen.Add(period))
            {
                keep.Add(backup.Name);
            }
        }
    }
}

/// <summary>When a plan runs next.</summary>
public static class BackupSchedule
{
    public static DateTime NextRunUtc(BackupPlan plan, DateTime afterUtc, TimeZoneInfo zone)
        => NextRunUtc(plan.Frequency, plan.EveryHours, plan.MinuteOfDay, plan.DayOfWeek, plan.DayOfMonth, afterUtc, zone);

    /// <summary>
    /// The first moment after <paramref name="afterUtc"/> that the schedule names, in the time zone of the installation (a time that does
    /// not exist on the day the clocks go forward is taken an hour later; one that exists twice, the first time). Hourly plans run every
    /// <paramref name="everyHours"/> hours counted from midnight, at the minute of <paramref name="minuteOfDay"/>.
    /// </summary>
    public static DateTime NextRunUtc(BackupFrequency frequency, int everyHours, int minuteOfDay, int dayOfWeek, int dayOfMonth, DateTime afterUtc, TimeZoneInfo zone)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        DateTime afterLocal = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
        int minutes = Math.Clamp(minuteOfDay, 0, 1439);

        foreach (DateTime local in Candidates(frequency, Math.Clamp(everyHours, 1, 24), minutes, dayOfWeek, Math.Clamp(dayOfMonth, 1, 28), afterLocal.Date))
        {
            DateTime utc = ToUtc(local, zone);
            if (utc > afterUtc)
            {
                return utc;
            }
        }

        throw new InvalidOperationException("The schedule has no next run.");   // cannot happen: every frequency names a time within a month
    }

    private static IEnumerable<DateTime> Candidates(BackupFrequency frequency, int everyHours, int minuteOfDay, int dayOfWeek, int dayOfMonth, DateTime firstDay)
    {
        switch (frequency)
        {
            case BackupFrequency.Hourly:
                for (int day = 0; day < 3; day++)
                {
                    for (int hour = 0; hour < 24; hour += everyHours)
                    {
                        yield return firstDay.AddDays(day).AddHours(hour).AddMinutes(minuteOfDay % 60);
                    }
                }

                break;

            case BackupFrequency.Daily:
                for (int day = 0; day < 3; day++)
                {
                    yield return firstDay.AddDays(day).AddMinutes(minuteOfDay);
                }

                break;

            case BackupFrequency.Weekly:
                for (int day = 0; day < 15; day++)
                {
                    DateTime date = firstDay.AddDays(day);
                    if ((int)date.DayOfWeek == ((dayOfWeek % 7) + 7) % 7)
                    {
                        yield return date.AddMinutes(minuteOfDay);
                    }
                }

                break;

            default:
                for (int month = 0; month < 3; month++)
                {
                    DateTime first = new DateTime(firstDay.Year, firstDay.Month, 1).AddMonths(month);
                    yield return first.AddDays(dayOfMonth - 1).AddMinutes(minuteOfDay);
                }

                break;
        }
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // the first of the two: the larger offset (summer time) is the earlier moment
            TimeSpan offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

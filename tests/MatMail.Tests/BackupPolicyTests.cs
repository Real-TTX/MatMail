using MatMail.Backup;
using MatMail.Data;

namespace MatMail.Tests;

public class BackupFileNameTests
{
    [Fact]
    public void A_name_carries_installation_origin_time_and_version()
    {
        string name = BackupFiles.Name(new DateTime(2026, 10, 8, 3, 15, 0, DateTimeKind.Utc), "0.1.42-20261007", encrypted: false, "p2", "3F9A1C77-0000-4000-8000-000000000000");

        Assert.Equal("matmail-3f9a1c-p2-20261008-031500-0.1.42-20261007.zip", name);
        BackupFileName parsed = BackupFiles.Parse(name)!;
        Assert.Equal("3f9a1c", parsed.Installation);
        Assert.Equal("p2", parsed.Label);
        Assert.True(parsed.IsPlan);
        Assert.Equal(new DateTime(2026, 10, 8, 3, 15, 0, DateTimeKind.Utc), parsed.CreatedUtc);
        Assert.Equal("0.1.42-20261007", parsed.Version);
        Assert.False(parsed.Encrypted);
    }

    [Fact]
    public void An_encrypted_backup_has_its_own_extension_and_manual_ones_are_no_plan()
    {
        string name = BackupFiles.Name(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "nightly-42-20261007", encrypted: true);

        Assert.EndsWith(".mmbak", name);
        BackupFileName parsed = BackupFiles.Parse(name)!;
        Assert.True(parsed.Encrypted);
        Assert.Equal(BackupFiles.ManualLabel, parsed.Label);
        Assert.False(parsed.IsPlan);
        Assert.Equal("000000", parsed.Installation);   // no installation id yet
        Assert.False(BackupFiles.Parse(BackupFiles.Name(DateTime.UtcNow, "1.0", false, BackupFiles.PreRestoreLabel, "abcdef12"))!.IsPlan);
    }

    [Theory]
    [InlineData("0.1.42+build.5", "0.1.42-build.5")]
    [InlineData("local build", "local-build")]
    public void A_version_with_odd_characters_is_cleaned(string version, string expected)
    {
        string name = BackupFiles.Name(DateTime.UtcNow, version, false, "p1", "abcdef");

        Assert.Equal(expected, BackupFiles.Parse(name)!.Version);
    }

    [Theory]
    [InlineData("3F9A1C77-aaaa", "3f9a1c")]
    [InlineData("12", "120000")]
    [InlineData("", "000000")]
    [InlineData(null, "000000")]
    [InlineData("xyz-12ab34", "12ab34")]
    public void The_installation_part_is_six_lower_case_hex_digits(string? id, string expected)
        => Assert.Equal(expected, BackupFiles.Short(id));

    [Theory]
    [InlineData("matmail-3f9a1c-p1-20261008-031500-1.0.zip.partial")]
    [InlineData("matmail-3f9a1c-p1-20261008-031500-1.0.tar")]
    [InlineData("matmail-xyz123-p1-20261008-031500-1.0.zip")]
    [InlineData("matmail-3f9a1c-weekly-20261008-031500-1.0.zip")]
    [InlineData("matmail-3f9a1c-p1-2026-10-08-1.0.zip")]
    [InlineData("matmail-20261008-031500-1.0.zip")]
    [InlineData("backup.zip")]
    [InlineData("../matmail-3f9a1c-p1-20261008-031500-1.0.zip")]
    [InlineData("")]
    public void Anything_else_is_no_backup_of_ours(string name)
    {
        Assert.Null(BackupFiles.Parse(name));
        Assert.False(BackupFiles.IsBackupFileName(name));
    }
}

public class BackupRetentionTests
{
    private static (string Name, DateTime CreatedUtc) Backup(int year, int month, int day, int hour = 3)
        => ($"b-{year}{month:D2}{day:D2}-{hour:D2}", new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc));

    private static List<(string Name, DateTime CreatedUtc)> Daily(DateTime first, int days)
        => Enumerable.Range(0, days).Select(i => first.AddDays(i)).Select(d => Backup(d.Year, d.Month, d.Day)).ToList();

    private static string[] Names(IEnumerable<(string Name, DateTime CreatedUtc)> backups, string[] expired)
        => backups.Select(b => b.Name).Except(expired).Order().ToArray();

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);   // a Friday

    [Fact]
    public void The_newest_ones_are_kept()
    {
        List<(string, DateTime)> backups = Daily(new DateTime(2026, 10, 1), 9);   // 1 to 9 October

        IReadOnlyList<string> expired = BackupRetention.Expired(backups, new RetentionPolicy(3, 0, 0, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(new[] { "b-20261001-03", "b-20261002-03", "b-20261003-03", "b-20261004-03", "b-20261005-03", "b-20261006-03" }, expired.Order().ToArray());
    }

    [Fact]
    public void Nothing_goes_when_there_are_not_more_than_wanted()
    {
        Assert.Empty(BackupRetention.Expired(Daily(new DateTime(2026, 10, 5), 5), new RetentionPolicy(7, 0, 0, 0), Now, TimeZoneInfo.Utc));
        Assert.Empty(BackupRetention.Expired([], new RetentionPolicy(7, 0, 0, 0), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_newest_backup_is_never_removed_whatever_the_rules_say()
    {
        List<(string, DateTime)> backups = Daily(new DateTime(2026, 10, 5), 5);

        IReadOnlyList<string> expired = BackupRetention.Expired(backups, new RetentionPolicy(0, 0, 0, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(4, expired.Count);
        Assert.DoesNotContain("b-20261009-03", expired);
    }

    [Fact]
    public void Daily_weekly_and_monthly_rules_add_up()
    {
        // 40 daily backups, 31 August to 9 October
        List<(string, DateTime)> backups = Daily(new DateTime(2026, 8, 31), 40);

        IReadOnlyList<string> expired = BackupRetention.Expired(backups, new RetentionPolicy(7, 0, 4, 3), Now, TimeZoneInfo.Utc);

        Assert.Equal(
            new[]
            {
                "b-20260831-03",                                                                     // newest of August
                "b-20260920-03", "b-20260927-03",                                                    // newest of the weeks of 14 and 21 September
                "b-20260930-03",                                                                     // newest of September
                "b-20261003-03", "b-20261004-03", "b-20261005-03", "b-20261006-03", "b-20261007-03", "b-20261008-03", "b-20261009-03",   // the last seven (4 October is also the newest of its week)
            },
            Names(backups, expired.ToArray()));
        Assert.Equal(40 - 11, expired.Count);
    }

    [Fact]
    public void Days_are_kept_for_as_many_days_as_asked()
    {
        // two backups a day for a week: the newest of each of the last 3 days stays (plus the newest overall)
        var backups = new List<(string, DateTime)>();
        for (int day = 3; day <= 9; day++)
        {
            backups.Add(Backup(2026, 10, day, 3));
            backups.Add(Backup(2026, 10, day, 15));
        }

        IReadOnlyList<string> expired = BackupRetention.Expired(backups, new RetentionPolicy(1, 3, 0, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(new[] { "b-20261007-15", "b-20261008-15", "b-20261009-15" }, Names(backups, expired.ToArray()));
    }

    [Fact]
    public void A_week_runs_from_monday_to_sunday()
    {
        // Sunday 4 October and Monday 5 October are different weeks
        List<(string, DateTime)> backups = [Backup(2026, 10, 4), Backup(2026, 10, 5), Backup(2026, 10, 9)];

        IReadOnlyList<string> expired = BackupRetention.Expired(backups, new RetentionPolicy(1, 0, 2, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(new[] { "b-20261004-03", "b-20261009-03" }, Names(backups, expired.ToArray()));   // this week: 9 October, last week: 4 October; 5 October is not the newest of its week
    }

    [Fact]
    public void The_day_changes_at_midnight_of_the_installation_not_of_utc()
    {
        // 23:30 UTC on Thursday is already Friday 01:30 in Berlin: the same day as the backup at 10:00 UTC on Friday
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        List<(string, DateTime)> backups = [Backup(2026, 10, 8, 23), Backup(2026, 10, 9, 10)];

        IReadOnlyList<string> inBerlin = BackupRetention.Expired(backups, new RetentionPolicy(1, 2, 0, 0), Now, berlin);
        IReadOnlyList<string> inUtc = BackupRetention.Expired(backups, new RetentionPolicy(1, 2, 0, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(new[] { "b-20261008-23" }, inBerlin);   // one backup that day: the newest
        Assert.Empty(inUtc);                                  // two days, one backup each
    }

    [Fact]
    public void A_plan_removes_only_what_is_given_to_it()
    {
        // the caller hands over the backups of one plan of one installation; others are never in the list, so never removed
        List<(string, DateTime)> backups = Daily(new DateTime(2026, 10, 1), 9);

        IReadOnlyList<string> expired = BackupRetention.Expired(backups.Take(4), new RetentionPolicy(2, 0, 0, 0), Now, TimeZoneInfo.Utc);

        Assert.Equal(2, expired.Count);
        Assert.All(expired, name => Assert.Contains(name, backups.Take(4).Select(b => b.Item1)));
    }
}

public class BackupScheduleTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Next(BackupFrequency frequency, DateTime afterUtc, int minuteOfDay = 180, int dayOfWeek = 1, int dayOfMonth = 1, int everyHours = 6, TimeZoneInfo? zone = null)
        => BackupSchedule.NextRunUtc(frequency, everyHours, minuteOfDay, dayOfWeek, dayOfMonth, afterUtc, zone ?? Berlin);

    [Fact]
    public void Daily_runs_at_the_time_of_the_installation()
    {
        // 9 October: summer time (UTC+2), 3:00 is 1:00 UTC
        Assert.Equal(Utc(2026, 10, 9, 1), Next(BackupFrequency.Daily, Utc(2026, 10, 9, 0)));
        // winter time (UTC+1)
        Assert.Equal(Utc(2026, 12, 1, 2), Next(BackupFrequency.Daily, Utc(2026, 12, 1, 0)));
    }

    [Fact]
    public void The_next_run_is_always_after_the_given_moment()
    {
        Assert.Equal(Utc(2026, 10, 10, 1), Next(BackupFrequency.Daily, Utc(2026, 10, 9, 1)));
        Assert.Equal(Utc(2026, 10, 10, 1), Next(BackupFrequency.Daily, Utc(2026, 10, 9, 5)));
    }

    [Fact]
    public void A_time_that_does_not_exist_is_taken_an_hour_later()
    {
        // 28 March 2027: at 2:00 the clocks go to 3:00, so 2:30 does not exist
        Assert.Equal(Utc(2027, 3, 28, 1, 30), Next(BackupFrequency.Daily, Utc(2027, 3, 27, 12), minuteOfDay: 150));
    }

    [Fact]
    public void A_time_that_exists_twice_is_taken_the_first_time()
    {
        // 25 October 2026: at 3:00 the clocks go back to 2:00, so 2:30 happens twice (0:30 UTC and 1:30 UTC)
        Assert.Equal(Utc(2026, 10, 25, 0, 30), Next(BackupFrequency.Daily, Utc(2026, 10, 24, 12), minuteOfDay: 150));
    }

    [Fact]
    public void Weekly_runs_on_the_day_of_the_week()
    {
        // Friday 9 October -> Monday 12 October, 6:00 = 4:00 UTC
        Assert.Equal(Utc(2026, 10, 12, 4), Next(BackupFrequency.Weekly, Utc(2026, 10, 9, 12), minuteOfDay: 360, dayOfWeek: 1));
        // Sunday is 0 (and 7)
        Assert.Equal(Utc(2026, 10, 11, 4), Next(BackupFrequency.Weekly, Utc(2026, 10, 9, 12), minuteOfDay: 360, dayOfWeek: 0));
        Assert.Equal(Utc(2026, 10, 11, 4), Next(BackupFrequency.Weekly, Utc(2026, 10, 9, 12), minuteOfDay: 360, dayOfWeek: 7));
    }

    [Fact]
    public void Monthly_runs_on_the_day_of_the_month_and_never_later_than_the_28th()
    {
        Assert.Equal(Utc(2026, 11, 1, 2), Next(BackupFrequency.Monthly, Utc(2026, 10, 9), dayOfMonth: 1));    // 1 November 3:00 is winter time
        Assert.Equal(Utc(2026, 10, 28, 2), Next(BackupFrequency.Monthly, Utc(2026, 10, 9), dayOfMonth: 31));  // 31 becomes 28
        Assert.Equal(Utc(2027, 1, 1, 2), Next(BackupFrequency.Monthly, Utc(2026, 12, 15), dayOfMonth: 1));    // into the next year
    }

    [Fact]
    public void Hourly_runs_every_few_hours_counted_from_midnight()
    {
        // every 6 hours at minute 15: 0:15, 6:15, 12:15, 18:15 local time (10:20 local is 8:20 UTC)
        Assert.Equal(Utc(2026, 10, 9, 10, 15), Next(BackupFrequency.Hourly, Utc(2026, 10, 9, 8, 20), minuteOfDay: 15, everyHours: 6));
        Assert.Equal(Utc(2026, 10, 9, 16, 15), Next(BackupFrequency.Hourly, Utc(2026, 10, 9, 10, 15), minuteOfDay: 15, everyHours: 6));
        // after the last one of the day: the first of the next
        Assert.Equal(Utc(2026, 10, 9, 22, 15), Next(BackupFrequency.Hourly, Utc(2026, 10, 9, 17, 0), minuteOfDay: 15, everyHours: 6));
        // every hour
        Assert.Equal(Utc(2026, 10, 9, 8, 30), Next(BackupFrequency.Hourly, Utc(2026, 10, 9, 8, 20), minuteOfDay: 30, everyHours: 1));
    }

    [Fact]
    public void A_plan_knows_its_own_next_run()
    {
        var plan = new BackupPlan { Frequency = BackupFrequency.Daily, MinuteOfDay = 180 };

        Assert.Equal(Utc(2026, 10, 9, 1), BackupSchedule.NextRunUtc(plan, Utc(2026, 10, 9, 0), Berlin));
    }

    [Fact]
    public void Utc_installations_need_no_conversion()
    {
        Assert.Equal(Utc(2026, 10, 9, 3), Next(BackupFrequency.Daily, Utc(2026, 10, 9, 0), zone: TimeZoneInfo.Utc));
    }
}

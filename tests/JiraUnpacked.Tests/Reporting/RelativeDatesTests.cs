using System.Globalization;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// The date arithmetic behind the relative operators, with "now" and the zone as arguments.
/// Every expected instant here was worked out from a calendar, not read back from the code.
/// </summary>
public sealed class RelativeDatesTests
{
    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DateTime Day(string yyyyMmDd) =>
        DateTime.ParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static TimeZoneInfo Zone(string id)
    {
        Assert.True(RelativeDates.TryResolveZone(id, out var zone), $"'{id}' should resolve on this host.");
        return zone;
    }

    [Theory]
    [InlineData("hours", 5, 2026, 3, 31, 7)]
    [InlineData("days", 14, 2026, 3, 17, 12)]
    [InlineData("weeks", 2, 2026, 3, 17, 12)]
    [InlineData("months", 1, 2026, 2, 28, 12)]   // calendar months: March 31 minus one month is February 28
    public void Subtract_counts_back_from_now_in_each_unit(string unit, int amount, int y, int m, int d, int h)
    {
        var since = RelativeDates.Subtract(U(2026, 3, 31, 12), amount, unit);

        Assert.Equal(U(y, m, d, h), since);
        Assert.Equal(DateTimeKind.Utc, since.Kind);   // Npgsql refuses a non-UTC DateTime for timestamptz
    }

    [Theory]
    [InlineData("today",       "2026-09-23", "2026-09-24")]
    [InlineData("yesterday",   "2026-09-22", "2026-09-23")]
    [InlineData("thisWeek",    "2026-09-21", "2026-09-28")]
    [InlineData("lastWeek",    "2026-09-14", "2026-09-21")]
    [InlineData("thisMonth",   "2026-09-01", "2026-10-01")]
    [InlineData("lastMonth",   "2026-08-01", "2026-09-01")]
    [InlineData("thisQuarter", "2026-07-01", "2026-10-01")]
    [InlineData("lastQuarter", "2026-04-01", "2026-07-01")]
    [InlineData("thisYear",    "2026-01-01", "2027-01-01")]
    [InlineData("lastYear",    "2025-01-01", "2026-01-01")]
    public void Every_period_is_a_half_open_range_of_whole_days(string period, string start, string end)
    {
        var (s, e) = RelativeDates.Period(U(2026, 9, 23, 12), period, TimeZoneInfo.Utc);   // a Wednesday

        Assert.Equal(Day(start), s);
        Assert.Equal(Day(end), e);
        Assert.Equal(DateTimeKind.Utc, s.Kind);
        Assert.Equal(DateTimeKind.Utc, e.Kind);
    }

    [Theory]
    [InlineData(27, "2026-09-21")]   // Sunday: still the week that began on Monday the 21st
    [InlineData(28, "2026-09-28")]   // Monday: a new week
    public void Weeks_start_on_monday(int dayOfSeptember, string monday)
    {
        var (s, e) = RelativeDates.Period(U(2026, 9, dayOfSeptember, 12), "thisWeek", TimeZoneInfo.Utc);

        Assert.Equal(Day(monday), s);
        Assert.Equal(Day(monday).AddDays(7), e);
    }

    [Fact]
    public void Quarters_turn_over_at_midnight_on_the_first_of_the_month()
    {
        Assert.Equal((Day("2026-07-01"), Day("2026-10-01")),
            RelativeDates.Period(U(2026, 9, 30, 23, 59), "thisQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-10-01"), Day("2027-01-01")),
            RelativeDates.Period(U(2026, 10, 1), "thisQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-07-01"), Day("2026-10-01")),
            RelativeDates.Period(U(2026, 10, 1), "lastQuarter", TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_last_periods_reach_back_across_the_year_boundary()
    {
        var newYear = U(2027, 1, 1, 0, 30);   // a Friday

        Assert.Equal((Day("2026-12-31"), Day("2027-01-01")), RelativeDates.Period(newYear, "yesterday", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-28"), Day("2027-01-04")), RelativeDates.Period(newYear, "thisWeek", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-21"), Day("2026-12-28")), RelativeDates.Period(newYear, "lastWeek", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastMonth", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-10-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-01-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastYear", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Today_is_the_zones_day_not_utcs()
    {
        // 23:30 UTC on the 23rd is already 01:30 on the 24th in Warsaw (CEST, UTC+2).
        var (s, e) = RelativeDates.Period(U(2026, 9, 23, 23, 30), "today", Zone("Europe/Warsaw"));

        Assert.Equal(U(2026, 9, 23, 22), s);
        Assert.Equal(U(2026, 9, 24, 22), e);
    }

    [Fact]
    public void A_period_that_spans_a_dst_change_uses_each_ends_own_offset()
    {
        // October in Warsaw starts in summer time (UTC+2) and ends in winter time (UTC+1).
        var (s, e) = RelativeDates.Period(U(2026, 10, 15, 12), "thisMonth", Zone("Europe/Warsaw"));

        Assert.Equal(U(2026, 9, 30, 22), s);
        Assert.Equal(U(2026, 10, 31, 23), e);
    }

    [Fact]
    public void A_midnight_skipped_by_dst_starts_the_day_at_the_first_time_that_exists()
    {
        // Some zones spring forward AT midnight, so 00:00 never happens that day, and converting
        // it throws. Built here rather than taken from the host's zone data, which differs between
        // Windows and Linux and changes whenever a government does.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/MidnightDst", TimeSpan.FromHours(-4), "Midnight DST", "Midnight Standard", "Midnight Daylight",
            new[]
            {
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25)),
            });

        var (s, e) = RelativeDates.Period(U(2026, 3, 29, 12), "today", zone);

        Assert.Equal(U(2026, 3, 29, 4), s);   // 01:00 local, now UTC-3
        Assert.Equal(U(2026, 3, 30, 3), e);   // the next midnight, UTC-3
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_zone_is_utc(string? id)
    {
        Assert.True(RelativeDates.TryResolveZone(id, out var zone));
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Theory]
    [InlineData("Europe/Warsaw")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public void Iana_ids_resolve(string id) => Assert.Equal(id, Zone(id).Id);

    [Theory]
    [InlineData("Nope/Nowhere")]                     // well-formed, but no such zone
    [InlineData("../../etc/passwd")]                 // a path: never handed to the OS
    [InlineData("Central European Standard Time")]   // a Windows id: resolves on Windows only
    [InlineData("Europe/Warsaw\n")]                  // a trailing newline is not part of an id
    public void Anything_else_is_refused_on_every_host(string id) =>
        Assert.False(RelativeDates.TryResolveZone(id, out _));

    [Fact]
    public void An_id_longer_than_any_real_one_is_refused() =>
        Assert.False(RelativeDates.TryResolveZone(new string('A', 65), out _));
}

using System.Text.RegularExpressions;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The date arithmetic behind InLast / NotInLast / InPeriod. Pure: "now" and the zone are
/// arguments, so every boundary is testable without a clock. The translator calls it at query
/// time, so a saved "this week" means this week whenever it is opened.
/// </summary>
internal static class RelativeDates
{
    internal const int MaxAmount = 3650;

    internal static readonly IReadOnlyList<string> Units = new[] { "hours", "days", "weeks", "months" };

    /// <summary>Reading order — the order the SPA offers them in.</summary>
    internal static readonly IReadOnlyList<string> Periods = new[]
    {
        "today", "yesterday", "thisWeek", "lastWeek", "thisMonth", "lastMonth",
        "thisQuarter", "lastQuarter", "thisYear", "lastYear",
    };

    private const int MaxZoneIdLength = 64;

    /// <summary>
    /// IANA-shaped: a letter first, then letters, digits, _ + - in up to three /-separated parts
    /// ("UTC", "Europe/Warsaw", "America/Argentina/Buenos_Aires", "Etc/GMT+5"). Checked before the
    /// id reaches the OS: on Linux FindSystemTimeZoneById reads a file named after it, and a
    /// Windows id ("Central European Standard Time") resolves on Windows only — a saved view must
    /// mean the same thing on every host.
    /// </summary>
    private static readonly Regex IanaShape =
        new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}\z", RegexOptions.CultureInvariant);

    /// <summary>now − amount·unit. Months are calendar months (AddMonths clamps Mar 31 − 1 to Feb 28).</summary>
    internal static DateTime Subtract(DateTime nowUtc, int amount, string unit)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return unit switch
        {
            "hours" => now.AddHours(-amount),
            "days" => now.AddDays(-amount),
            "weeks" => now.AddDays(-7 * amount),
            "months" => now.AddMonths(-amount),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a relative-date unit."),
        };
    }

    /// <summary>A missing id is UTC. False = not an id this host can resolve; the caller answers 400.</summary>
    internal static bool TryResolveZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id)) return true;
        if (id.Length > MaxZoneIdLength || !IanaShape.IsMatch(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    /// <summary>
    /// The calendar period containing now, in the zone, as a half-open UTC range [start, end).
    /// Weeks start on Monday (ISO 8601).
    /// </summary>
    internal static (DateTime StartUtc, DateTime EndUtc) Period(DateTime nowUtc, string period, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var quarter = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        var year = new DateTime(today.Year, 1, 1);

        var (start, end) = period switch
        {
            "today" => (today, today.AddDays(1)),
            "yesterday" => (today.AddDays(-1), today),
            "thisWeek" => (monday, monday.AddDays(7)),
            "lastWeek" => (monday.AddDays(-7), monday),
            "thisMonth" => (month, month.AddMonths(1)),
            "lastMonth" => (month.AddMonths(-1), month),
            "thisQuarter" => (quarter, quarter.AddMonths(3)),
            "lastQuarter" => (quarter.AddMonths(-3), quarter),
            "thisYear" => (year, year.AddYears(1)),
            "lastYear" => (year.AddYears(-1), year),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Not a calendar period."),
        };
        return (LocalMidnightToUtc(start, zone), LocalMidnightToUtc(end, zone));
    }

    /// <summary>
    /// A local midnight as a UTC instant. Where a DST jump skips midnight itself (some zones spring
    /// forward at 00:00), the day starts at the first local time that exists; converting the
    /// missing midnight directly would throw.
    /// </summary>
    private static DateTime LocalMidnightToUtc(DateTime local, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 16 && zone.IsInvalidTime(t); i++) t = t.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(t, zone);
    }
}

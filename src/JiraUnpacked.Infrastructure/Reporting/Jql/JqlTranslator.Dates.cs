using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// Dates: comparisons, Between, relative amounts and calendar periods. The readers here are shared
/// with history windows (.History), so a window reads exactly as a date condition does.
/// </summary>
public sealed partial class JqlTranslator
{
    private const string MonthsWarning = "JQL has no month unit for relative dates, so months are approximated as 30 days.";

    private const string PeriodWarning =
        "Jira evaluates calendar periods (startOfWeek() and the like) in the Jira user's own time zone, and starts weeks on their locale's first day; this list uses the condition's time zone and Monday.";

    private const string DateZoneWarning =
        "Dates are written in UTC; Jira reads them in your Jira profile's time zone, so day boundaries can shift by that offset.";

    /// <summary>
    /// A fixed date written into the JQL, with the one warning every such date needs: Jira reads a
    /// date literal in the Jira user's profile time zone, not in UTC as it is written.
    /// </summary>
    private static string DateLiteral(DateTime utc, Run run)
    {
        run.Warn(DateZoneWarning);
        return JqlText.Date(utc);
    }

    /// <summary>JQL's start/end functions for every calendar period but the quarters, which JQL has none for.</summary>
    private static readonly IReadOnlyDictionary<string, (string Unit, int Offset)> PeriodFunctions =
        new Dictionary<string, (string, int)>
        {
            ["today"] = ("Day", 0), ["yesterday"] = ("Day", -1),
            ["thisWeek"] = ("Week", 0), ["lastWeek"] = ("Week", -1),
            ["thisMonth"] = ("Month", 0), ["lastMonth"] = ("Month", -1),
            ["thisYear"] = ("Year", 0), ["lastYear"] = ("Year", -1),
        };

    /// <summary>A calendar period's JQL bounds: functions where JQL has them (end inclusive), fixed dates for quarters (end exclusive).</summary>
    private sealed record PeriodBounds(string Start, string End, bool EndInclusive);

    /// <summary>created / updated.</summary>
    private Clause DateField(string field, FilterCondition c, Run run)
    {
        switch (c.Op)
        {
            case FilterOperator.Between:
            {
                if (ReadRange(Bound(c.Values, 0), Bound(c.Values, 1)) is not { } range)
                    return Clause.Out("its dates are not valid");
                var (from, to, toExclusive) = range;
                var parts = new List<string>();
                if (from is { } lo) parts.Add($"{field} >= {DateLiteral(lo, run)}");
                if (to is { } hi) parts.Add($"{field} {(toExclusive ? "<" : "<=")} {DateLiteral(hi, run)}");
                // Both ends open matches every issue: a clause no issue fails says exactly that.
                return Clause.Of(parts.Count == 0 ? $"{field} is not EMPTY" : JqlText.Join(parts, "AND"));
            }
            case FilterOperator.InLast or FilterOperator.NotInLast:
                return Relative(c.Value, run) is { } offset
                    ? Clause.Of($"{field} {(c.Op == FilterOperator.InLast ? ">=" : "<")} {offset}")
                    : Clause.Out("it has no amount");
            case FilterOperator.InPeriod:
                return Period(c.Value, run, out var why) is { } p
                    ? Clause.Of($"({field} >= {p.Start} AND {field} {(p.EndInclusive ? "<=" : "<")} {p.End})")
                    : Clause.Out(why);
        }
        if (c.Value is not { } v || !TryReadDate(v, out var at, out _)) return Clause.Out("it has no date");
        var symbol = c.Op switch
        {
            FilterOperator.Lt => "<",
            FilterOperator.Lte => "<=",
            FilterOperator.Gt => ">",
            FilterOperator.Gte => ">=",
            _ => null,
        };
        return symbol is null ? Clause.Out($"JQL has no '{c.Op}' for {field}") : Clause.Of($"{field} {symbol} {DateLiteral(at, run)}");
    }

    private static JsonElement? Bound(List<JsonElement>? values, int at) =>
        values is { } vs && vs.Count > at && vs[at].ValueKind != JsonValueKind.Null ? vs[at] : null;

    /// <summary>
    /// As the filter reads a range — through its own reader, so the two cannot drift: UTC; a date-only
    /// `to` runs through that day (exclusive at the next midnight); through 9999-12-31 is an open end.
    /// Null when the filter would refuse the range (not dates, or `from` after `to`).
    /// </summary>
    private static (DateTime? From, DateTime? To, bool ToExclusive)? ReadRange(JsonElement? fromEl, JsonElement? toEl)
    {
        if (IssueFilterTranslator.ReadDateRange(fromEl, toEl, "", "", out var from, out var to, out var toDateOnly) is not null)
            return null;
        return to is { } end && toDateOnly ? (from, end.AddDays(1), true) : (from, to, false);
    }

    /// <summary>An ISO-8601 date or timestamp, read as UTC like every date in the filter (the filter's own reader).</summary>
    private static bool TryReadDate(JsonElement v, out DateTime value, out bool dateOnly) =>
        IssueFilterTranslator.TryReadDate(v, out value, out dateOnly);

    /// <summary>{"amount", "unit"} → a JQL relative offset ("-14d"); months as 30-day blocks, with a warning.</summary>
    private static string? Relative(JsonElement? value, Run run)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out var n)
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return null;
        switch (u.GetString())
        {
            case "hours":
                return $"-{n}h";
            case "days":
                return $"-{n}d";
            case "weeks":
                return $"-{n}w";
            case "months":
                run.Warn(MonthsWarning);
                return $"-{n * 30}d";
            default:
                return null;
        }
    }

    /// <summary>{"period", "timeZone"?} → JQL bounds. Quarters are computed now, in the condition's zone, exactly as the filter computes them.</summary>
    private PeriodBounds? Period(JsonElement? value, Run run) => Period(value, run, out _);

    /// <summary>As above; when there are no bounds, <paramref name="why"/> says which part could not be read.</summary>
    private PeriodBounds? Period(JsonElement? value, Run run, out string why)
    {
        why = "it has no period";
        if (value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return null;
        var period = pe.GetString()!;
        var isFunction = PeriodFunctions.TryGetValue(period, out var fn);
        if (!isFunction && !RelativeDates.Periods.Contains(period)) return null;

        // The zone is read for every period, as the filter reads it (absent or null is UTC; anything
        // but a known IANA id is refused) — only the quarters then use it, the functions have none.
        var hasZone = v.TryGetProperty("timeZone", out var tz) && tz.ValueKind != JsonValueKind.Null;
        var zoneId = hasZone && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null;
        if ((hasZone && tz.ValueKind != JsonValueKind.String) || !RelativeDates.TryResolveZone(zoneId, out var zone))
        {
            why = "its time zone is not known";
            return null;
        }

        if (isFunction)
        {
            run.Warn(PeriodWarning);
            var arg = fn.Offset == 0 ? "" : fn.Offset.ToString(CultureInfo.InvariantCulture);
            return new PeriodBounds($"startOf{fn.Unit}({arg})", $"endOf{fn.Unit}({arg})", EndInclusive: true);
        }
        var (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        run.Warn($"JQL has no quarter functions, so {(period == "thisQuarter" ? "this" : "last")} quarter is written as fixed dates from {JqlText.Date(start)} to {JqlText.Date(end)}: the JQL will not move with the calendar.");
        return new PeriodBounds(DateLiteral(start, run), DateLiteral(end, run), EndInclusive: false);
    }
}

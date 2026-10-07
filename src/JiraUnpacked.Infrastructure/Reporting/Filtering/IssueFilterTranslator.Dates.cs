using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Date fields: comparisons, Between, and the relative operators resolved against the clock. The
/// readers (ReadSince, ReadPeriod, ReadDateRange) are shared with history windows ('during'), so
/// "in the last", "in period" and "between" follow one set of rules wherever they appear.
/// </summary>
public sealed partial class IssueFilterTranslator
{
    private readonly IClock _clock;

    /// <summary>
    /// The clock is what makes a relative condition relative: "in the last 14 days" is resolved
    /// against it on every build, so a saved view means the same thing whenever it is opened.
    /// </summary>
    public IssueFilterTranslator(IClock clock) => _clock = clock;

    private (Expression<Func<Issue, bool>>?, string?) BuildDate(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, DateTime>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];

        switch (c.Op)
        {
            case FilterOperator.Between: return BuildDateBetween(c, body, p);
            case FilterOperator.InLast or FilterOperator.NotInLast: return BuildDateInLast(c, body, p);
            case FilterOperator.InPeriod: return BuildDateInPeriod(c, body, p);
        }

        if (c.Value is not { ValueKind: JsonValueKind.String } sv || !DateTime.TryParse(
                sv.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return (null, $"Field '{c.Field}' requires an ISO-8601 date 'value'.");
        var k = Expression.Constant(dt, typeof(DateTime));
        Expression cmp = c.Op switch
        {
            FilterOperator.Lt => Expression.LessThan(body, k),
            FilterOperator.Lte => Expression.LessThanOrEqual(body, k),
            FilterOperator.Gt => Expression.GreaterThan(body, k),
            FilterOperator.Gte => Expression.GreaterThanOrEqual(body, k),
            _ => Expression.Constant(false),
        };
        return (Lambda(cmp, p), null);
    }

    private static readonly Regex DateOnlyShape = new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);

    /// <summary>An ISO-8601 date or timestamp, read as UTC like every other date here.</summary>
    private static bool TryReadDate(JsonElement v, out DateTime value, out bool dateOnly)
    {
        value = default; dateOnly = false;
        if (v.ValueKind != JsonValueKind.String) return false;
        var s = v.GetString()!;
        dateOnly = DateOnlyShape.IsMatch(s);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>`from ≤ x ≤ to`, either bound open; see ReadDateRange and DateRangeTest.</summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDateBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        if (ReadDateRange(fromEl, toEl, $"'Between' on '{c.Field}'", "'values'",
                out var from, out var to, out var toDateOnly) is { } error)
            return (null, error);
        // Only an open-ended `to` with no `from` leaves nothing to compare: every issue matches.
        return (Lambda(DateRangeTest(body, from, to, toDateOnly, d => Expression.Constant(d))
                       ?? Expression.Constant(true), p), null);
    }

    /// <summary>
    /// [from, to] as ISO-8601 dates or timestamps (UTC), either absent. A date-only `to` means
    /// "through that day" (DateRangeTest adds the day); `from` after the effective end is refused.
    /// </summary>
    private static string? ReadDateRange(
        JsonElement? fromEl, JsonElement? toEl, string what, string where,
        out DateTime? from, out DateTime? to, out bool toDateOnly)
    {
        from = null; to = null; toDateOnly = false;
        var notDates = $"{what} requires ISO-8601 dates (or null) in {where}.";
        if (fromEl is { } f)
        {
            if (!TryReadDate(f, out var d, out _)) return notDates;
            from = d;
        }
        if (toEl is { } t)
        {
            if (!TryReadDate(t, out var d, out toDateOnly)) return notDates;
            to = d;
        }
        // "Through 9999-12-31" is through the last day a DateTime can hold: it has no next midnight
        // (AddDays would overflow), and it excludes no instant at all — so it is an open end,
        // which is exactly what it means, rather than a 400 for a date the parser accepted.
        if (toDateOnly && to!.Value.Date == DateTime.MaxValue.Date) { to = null; toDateOnly = false; }

        // Judged against the effective end, so "from 10:00 on the 30th to the 30th" is not reversed.
        if (from is { } a && to is { } b && (toDateOnly ? a >= b.AddDays(1) : a > b))
            return $"{what}: 'from' is after 'to'.";
        return null;
    }

    /// <summary>
    /// `from ≤ x`, and `x &lt; to + 1 day` for a date-only `to` or `x ≤ to` for a timestamp.
    /// <paramref name="value"/> decides how a bound enters the tree (constant or parameter).
    /// Null when both ends are open.
    /// </summary>
    private static Expression? DateRangeTest(
        Expression body, DateTime? from, DateTime? to, bool toDateOnly, Func<DateTime, Expression> value)
    {
        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, value(lo));
        if (to is { } hi)
            cmp = And(cmp, toDateOnly
                ? Expression.LessThan(body, value(hi.AddDays(1)))
                : Expression.LessThanOrEqual(body, value(hi)));
        return cmp;
    }

    /// <summary>InLast: `x ≥ now − amount·unit`. NotInLast: `x < now − amount·unit` ("more than … ago").</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInLast(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        var shape = $"'{c.Op}' on '{c.Field}' requires 'value': {{\"amount\": 14, \"unit\": \"days\"}}.";
        if (ReadSince(c.Value ?? default, shape, out var since) is { } error) return (null, error);

        // A parameter, not a constant: the boundary moves on every request, and a constant would
        // make each request a new query to EF — a compiled-query cache entry per tick, never reused.
        var at = Parameter(since);
        return (Lambda(c.Op == FilterOperator.InLast
            ? Expression.GreaterThanOrEqual(body, at)
            : Expression.LessThan(body, at), p), null);
    }

    /// <summary>{"amount", "unit"} → now − amount·unit. Null = read; otherwise the 400's message.</summary>
    private string? ReadSince(JsonElement v, string shapeError, out DateTime since)
    {
        since = default;
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return shapeError;
        if (!a.TryGetInt32(out var amount) || amount < 1 || amount > RelativeDates.MaxAmount)
            return $"'amount' must be a whole number from 1 to {RelativeDates.MaxAmount}.";
        var unit = u.GetString()!;
        if (!RelativeDates.Units.Contains(unit))
            return $"Unknown unit '{unit}'. Use one of: {string.Join(", ", RelativeDates.Units)}.";
        since = RelativeDates.Subtract(_clock.UtcNow, amount, unit);
        return null;
    }

    /// <summary>`start ≤ x < end` of the calendar period containing now, in the condition's zone (missing = UTC).</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInPeriod(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        var shape = $"'InPeriod' on '{c.Field}' requires 'value': {{\"period\": \"thisWeek\", \"timeZone\": \"Europe/Warsaw\"}}.";
        if (ReadPeriod(c.Value ?? default, shape, out var start, out var end) is { } error) return (null, error);
        return (Lambda(Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(start)),
            Expression.LessThan(body, Expression.Constant(end))), p), null);
    }

    /// <summary>{"period", "timeZone"?} → the period's [start, end) in UTC. Null = read; otherwise the 400's message.</summary>
    private string? ReadPeriod(JsonElement v, string shapeError, out DateTime start, out DateTime end)
    {
        start = default; end = default;
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return shapeError;
        var period = pe.GetString()!;
        if (!RelativeDates.Periods.Contains(period))
            return $"Unknown period '{period}'. Use one of: {string.Join(", ", RelativeDates.Periods)}.";

        string? zoneId = null;
        if (v.TryGetProperty("timeZone", out var tz) && tz.ValueKind != JsonValueKind.Null)
        {
            if (tz.ValueKind != JsonValueKind.String)
                return "'timeZone' must be an IANA time zone id, such as \"Europe/Warsaw\".";
            zoneId = tz.GetString();
        }
        if (!RelativeDates.TryResolveZone(zoneId, out var zone))
            return $"Unknown time zone '{zoneId}'. Use an IANA id, such as \"Europe/Warsaw\".";

        (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        return null;
    }
}

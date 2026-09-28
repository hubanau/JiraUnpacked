using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

public sealed record FilterBuildResult(Expression<Func<Issue, bool>>? Predicate, string? Error);

/// <summary>
/// Resolves display names to surrogate Ids (tenant-scoped, injected by the service). `field` is a
/// dict field's name — or one of the closed <see cref="FilterLookups"/> keys, for lookups that
/// are not by display name. Nothing a caller sends ever becomes a lookup key.
/// </summary>
public delegate Task<IReadOnlyList<Guid>> DictNameResolver(
    string field, IReadOnlyList<string> names, CancellationToken ct);

/// <summary>
/// Lookups the translator asks the resolver for besides display names. A closed set: the
/// compiler's resolver has an arm for each, over the same tenant-scoped context as the names.
/// </summary>
public static class FilterLookups
{
    /// <summary>names = sprint states ("active", "future", "closed") → ids of the sprints in them.</summary>
    public const string SprintState = "sprint.state";
}

public interface IIssueFilterTranslator
{
    /// <summary>Flat AND over a condition list — the original FR-E4 shape. Empty = unfiltered.</summary>
    Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default);

    /// <summary>Boolean tree. Every group must be non-empty; the guards are counted over the whole tree.</summary>
    Task<FilterBuildResult> BuildAsync(
        FilterGroup group, DictNameResolver resolveDict, CancellationToken ct = default);
}

public sealed class IssueFilterTranslator : IIssueFilterTranslator
{
    public const int MaxConditions = 20;
    public const int MaxInValues = 100;
    public const int MaxGroups = 10;
    public const int MaxDepth = 3;

    private readonly IClock _clock;

    /// <summary>
    /// The clock is what makes a relative condition relative: "in the last 14 days" is resolved
    /// against it on every build, so a saved view means the same thing whenever it is opened.
    /// </summary>
    public IssueFilterTranslator(IClock clock) => _clock = clock;

    /// <summary>Running totals for the guards. Counted over the WHOLE tree: a per-node limit is
    /// defeated by adding nodes, which is the shape a hostile request would take.</summary>
    private sealed class Budget { public int Conditions; public int Groups; }

    public async Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default)
    {
        // Preserved verbatim from FR-E4: an omitted or empty 'conditions' has always meant
        // "no filter", never "an empty group". Only a group a caller actually wrote is rejected
        // for being empty (see BuildGroupAsync).
        if (conditions.Count == 0) return new FilterBuildResult(MatchAll, null);

        return await BuildAsync(
            new FilterGroup(FilterLogic.And, conditions.ToList(), null), resolveDict, ct);
    }

    public async Task<FilterBuildResult> BuildAsync(
        FilterGroup group, DictNameResolver resolveDict, CancellationToken ct = default)
    {
        // A root switched off — or one whose every part is switched off — is "no filter": the
        // user turned the parts off on purpose. A group WRITTEN empty is still refused below.
        if (group.Disabled == true) return new FilterBuildResult(MatchAll, null);

        var (predicate, error) = await BuildGroupAsync(group, resolveDict, new Budget(), 1, ct);
        return error is not null ? Err(error) : new FilterBuildResult(predicate ?? MatchAll, null);
    }

    /// <summary>
    /// A null Predicate with a null Error means every part of this group is switched off: the
    /// group is absent, and its parent joins only its other parts.
    /// </summary>
    private async Task<(Expression<Func<Issue, bool>>? Predicate, string? Error)> BuildGroupAsync(
        FilterGroup group, DictNameResolver resolveDict, Budget budget, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
            return (null, $"A filter may nest at most {MaxDepth} levels deep.");
        if (++budget.Groups > MaxGroups)
            return (null, $"A filter may contain at most {MaxGroups} groups.");

        var conditions = group.Conditions ?? new List<FilterCondition>();
        var groups = group.Groups ?? new List<FilterGroup>();

        // An empty And is vacuously true and an empty Or is vacuously false. Same client bug,
        // opposite blast radius, and neither is what the caller meant — so refuse both. Checked
        // on what was WRITTEN, before disabled parts are set aside.
        if (conditions.Count == 0 && groups.Count == 0)
            return (null, "A filter group must contain at least one condition or nested group.");

        var parts = new List<Expression<Func<Issue, bool>>>();

        // Disabled parts are set aside before anything looks at them: not validated (a row
        // switched off because it is broken must not break the filter) and not counted.
        foreach (var c in conditions.Where(c => c.Disabled != true))
        {
            if (++budget.Conditions > MaxConditions)
                return (null, $"A filter may have at most {MaxConditions} conditions.");

            var (predicate, error) = await BuildConditionAsync(c, resolveDict, ct);
            if (error is not null) return (null, error);
            parts.Add(predicate!);
        }

        foreach (var g in groups.Where(g => g.Disabled != true))
        {
            var (predicate, error) = await BuildGroupAsync(g, resolveDict, budget, depth + 1, ct);
            if (error is not null) return (null, error);
            if (predicate is not null) parts.Add(predicate);
        }

        if (parts.Count == 0) return (null, null);

        // Seedless Aggregate on purpose: seeding with `i => true` is harmless under AndAlso but
        // would make every Or group match everything. parts is non-empty by the check above.
        Func<Expression<Func<Issue, bool>>, Expression<Func<Issue, bool>>, Expression<Func<Issue, bool>>> combine =
            group.Logic == FilterLogic.Or ? OrElse : AndAlso;
        return (parts.Aggregate(combine), null);
    }

    private async Task<(Expression<Func<Issue, bool>>?, string?)> BuildConditionAsync(
        FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        if (!FilterFields.All.TryGetValue(c.Field, out var field))
            return (null, $"Unknown field '{c.Field}'.");
        if (!field.Ops.Contains(c.Op))
            return (null, $"Operator '{c.Op}' is not allowed for field '{c.Field}'.");

        return field.Type switch
        {
            FieldType.DictName  => await BuildDictAsync(field, c, resolveDict, ct),
            FieldType.Decimal   => BuildDecimal(field, c),
            FieldType.DateTime  => BuildDate(field, c),
            FieldType.Text      => BuildText(field, c),
            FieldType.ParentRef => BuildParent(field, c),
            _ => (null, "Unsupported field type."),
        };
    }

    private static readonly Expression<Func<Issue, bool>> MatchAll = i => true;

    private static FilterBuildResult Err(string m) => new(null, m);

    // ---- per-type builders ----

    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)
        var isEmpty = Expression.Equal(fkBody, NullGuid);

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(fkBody, NullGuid), p), null);

        var (pick, error) = ReadDictValues(field, c);
        if (error is not null) return (null, error);

        // Names and smart values resolve separately into ONE id set: "Sprint 7 or the active
        // sprint" is a single In. In [null] has nothing to resolve, so no round trip.
        var ids = new List<Guid>();
        if (pick!.Names.Count > 0) ids.AddRange(await resolveDict(field.Name, pick.Names, ct));
        if (pick.States.Count > 0)
            ids.AddRange(await resolveDict(FilterLookups.SprintState, pick.States.Distinct().ToList(), ct));

        // i.Fk.HasValue && ids.Contains(i.Fk.Value)
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        var contains = Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);
        var inSet = Expression.AndAlso(hasValue, contains);

        return c.Op switch
        {
            // One meaning of "empty" across the product: it matches only when asked for. "Is not"
            // and "is none of" therefore require a value — a NULL null-in-NotIn adds nothing.
            FilterOperator.Neq or FilterOperator.NotIn =>
                (Lambda(Expression.AndAlso(hasValue, Expression.Not(contains)), p), null),
            FilterOperator.In when pick.WantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    /// <summary>
    /// What a dictionary condition asked for: display names, sprint states (from smart values), and
    /// whether (Empty) — a JSON null inside 'values' — was among them.
    /// </summary>
    private sealed record DictPick(List<string> Names, List<string> States, bool WantsEmpty);

    private static (DictPick?, string?) ReadDictValues(FilterField field, FilterCondition c)
    {
        var pick = new DictPick(new List<string>(), new List<string>(), false);

        if (c.Op is FilterOperator.In or FilterOperator.NotIn)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            var wantsEmpty = false;
            foreach (var v in c.Values)
            {
                switch (v.ValueKind)
                {
                    case JsonValueKind.Null:
                        wantsEmpty = true;
                        break;
                    case JsonValueKind.String:
                        pick.Names.Add(v.GetString()!);
                        break;
                    case JsonValueKind.Object:
                        if (ReadSmart(field, v, pick.States) is { } smartError) return (null, smartError);
                        break;
                    default:
                        return (null, $"Field '{c.Field}' values must be strings, or null for (Empty).");
                }
            }
            return (pick with { WantsEmpty = wantsEmpty }, null);
        }

        // Eq / Neq: one name, or one smart value.
        if (c.Value is { ValueKind: JsonValueKind.String } sv)
        {
            pick.Names.Add(sv.GetString()!);
        }
        else if (c.Value is { ValueKind: JsonValueKind.Object } ov)
        {
            if (ReadSmart(field, ov, pick.States) is { } smartError) return (null, smartError);
        }
        else
        {
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        }
        return (pick, null);
    }

    /// <summary>A smart value, {"smart": "active"}: valid on sprint only, and only with a known key.</summary>
    private static string? ReadSmart(FilterField field, JsonElement v, List<string> states)
    {
        if (field.Name != "sprint")
            return $"Smart values are only available on 'sprint', not '{field.Name}'.";
        if (!v.TryGetProperty("smart", out var key) || key.ValueKind != JsonValueKind.String)
            return "A smart value must look like {\"smart\": \"active\"}.";
        var smart = SmartSprints.Find(key.GetString()!);
        if (smart is null)
            return $"Unknown smart value '{key.GetString()}'. Use one of: {string.Join(", ", SmartSprints.All.Select(s => s.Key))}.";
        states.AddRange(smart.States);
        return null;
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimal(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, decimal?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        var notNull = Expression.NotEqual(body, NullDecimal);
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullDecimal), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);
        if (c.Op == FilterOperator.Between) return BuildDecimalBetween(c, body, p);
        if (c.Value is not { ValueKind: JsonValueKind.Number } nv || !nv.TryGetDecimal(out var d))
            return (null, $"Field '{c.Field}' requires a numeric 'value'.");
        var k = Expression.Constant((decimal?)d, typeof(decimal?));
        Expression cmp = c.Op switch
        {
            FilterOperator.Eq => Expression.Equal(body, k),
            // Guarded explicitly: under C# null semantics, which EF reproduces in SQL, a bare
            // `StoryPoints != k` is TRUE for NULL — "is not 5" would claim every unestimated issue.
            FilterOperator.Neq => Expression.AndAlso(notNull, Expression.NotEqual(body, k)),
            FilterOperator.Lt => Expression.LessThan(body, k),
            FilterOperator.Lte => Expression.LessThanOrEqual(body, k),
            FilterOperator.Gt => Expression.GreaterThan(body, k),
            FilterOperator.Gte => Expression.GreaterThanOrEqual(body, k),
            _ => Expression.Constant(false),
        };
        return (Lambda(cmp, p), null);
    }

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

    // ---- ranges and relative dates ----

    /// <summary>`values: [from, to]` — exactly two; either may be JSON null (open), not both.</summary>
    private static string? ReadBounds(FilterCondition c, out JsonElement? from, out JsonElement? to)
    {
        from = null; to = null;
        if (c.Values is not { Count: 2 })
            return $"'Between' on '{c.Field}' requires 'values': [from, to].";
        if (c.Values[0].ValueKind != JsonValueKind.Null) from = c.Values[0];
        if (c.Values[1].ValueKind != JsonValueKind.Null) to = c.Values[1];
        return from is null && to is null
            ? $"'Between' on '{c.Field}' needs at least one bound; both are empty."
            : null;
    }

    /// <summary>Joins an optional left part with a right part.</summary>
    private static Expression And(Expression? a, Expression b) => a is null ? b : Expression.AndAlso(a, b);

    /// <summary>
    /// `from ≤ x ≤ to`, either bound open. An unestimated issue never matches: the lifted
    /// comparison is false for NULL, in memory and in SQL alike.
    /// </summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimalBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        var notNumbers = $"'Between' on '{c.Field}' requires numbers (or null) in 'values'.";

        decimal? from = null, to = null;
        if (fromEl is { } f)
        {
            if (f.ValueKind != JsonValueKind.Number || !f.TryGetDecimal(out var d)) return (null, notNumbers);
            from = d;
        }
        if (toEl is { } t)
        {
            if (t.ValueKind != JsonValueKind.Number || !t.TryGetDecimal(out var d)) return (null, notNumbers);
            to = d;
        }
        if (from > to) return (null, $"'Between' on '{c.Field}': 'from' is greater than 'to'.");

        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, Expression.Constant((decimal?)lo, typeof(decimal?)));
        if (to is { } hi) cmp = And(cmp, Expression.LessThanOrEqual(body, Expression.Constant((decimal?)hi, typeof(decimal?))));
        return (Lambda(cmp!, p), null);
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

    /// <summary>
    /// `from ≤ x ≤ to`. A date-only `to` means "through that day": everything before the next
    /// midnight (UTC). A full timestamp is used as-is.
    /// </summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDateBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        var notDates = $"'Between' on '{c.Field}' requires ISO-8601 dates (or null) in 'values'.";

        DateTime? from = null, to = null;
        var toDateOnly = false;
        if (fromEl is { } f)
        {
            if (!TryReadDate(f, out var d, out _)) return (null, notDates);
            from = d;
        }
        if (toEl is { } t)
        {
            if (!TryReadDate(t, out var d, out toDateOnly)) return (null, notDates);
            to = d;
        }
        // Judged against the effective end, so "from 10:00 on the 30th to the 30th" is not reversed.
        if (from is { } a && to is { } b && (toDateOnly ? a >= b.AddDays(1) : a > b))
            return (null, $"'Between' on '{c.Field}': 'from' is after 'to'.");

        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, Expression.Constant(lo));
        if (to is { } hi)
            cmp = And(cmp, toDateOnly
                ? Expression.LessThan(body, Expression.Constant(hi.AddDays(1)))
                : Expression.LessThanOrEqual(body, Expression.Constant(hi)));
        return (Lambda(cmp!, p), null);
    }

    /// <summary>InLast: `x ≥ now − amount·unit`. NotInLast: `x < now − amount·unit` ("more than … ago").</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInLast(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return (null, $"'{c.Op}' on '{c.Field}' requires 'value': {{\"amount\": 14, \"unit\": \"days\"}}.");
        if (!a.TryGetInt32(out var amount) || amount < 1 || amount > RelativeDates.MaxAmount)
            return (null, $"'amount' must be a whole number from 1 to {RelativeDates.MaxAmount}.");
        var unit = u.GetString()!;
        if (!RelativeDates.Units.Contains(unit))
            return (null, $"Unknown unit '{unit}'. Use one of: {string.Join(", ", RelativeDates.Units)}.");

        var since = Expression.Constant(RelativeDates.Subtract(_clock.UtcNow, amount, unit));
        return (Lambda(c.Op == FilterOperator.InLast
            ? Expression.GreaterThanOrEqual(body, since)
            : Expression.LessThan(body, since), p), null);
    }

    /// <summary>`start ≤ x < end` of the calendar period containing now, in the condition's zone (missing = UTC).</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInPeriod(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return (null, $"'InPeriod' on '{c.Field}' requires 'value': {{\"period\": \"thisWeek\", \"timeZone\": \"Europe/Warsaw\"}}.");
        var period = pe.GetString()!;
        if (!RelativeDates.Periods.Contains(period))
            return (null, $"Unknown period '{period}'. Use one of: {string.Join(", ", RelativeDates.Periods)}.");

        string? zoneId = null;
        if (v.TryGetProperty("timeZone", out var tz) && tz.ValueKind != JsonValueKind.Null)
        {
            if (tz.ValueKind != JsonValueKind.String)
                return (null, "'timeZone' must be an IANA time zone id, such as \"Europe/Warsaw\".");
            zoneId = tz.GetString();
        }
        if (!RelativeDates.TryResolveZone(zoneId, out var zone))
            return (null, $"Unknown time zone '{zoneId}'. Use an IANA id, such as \"Europe/Warsaw\".");

        var (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        return (Lambda(Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(start)),
            Expression.LessThan(body, Expression.Constant(end))), p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildText(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        var notNull = Expression.NotEqual(body, NullString);
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);

        if (c.Op is FilterOperator.In or FilterOperator.NotIn)   // jiraKey
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            var keys = new List<string>();
            foreach (var v in c.Values)
            {
                // A key is never empty, so there is no (Empty) to ask for: a null here is a client bug.
                if (v.ValueKind != JsonValueKind.String) return (null, $"Field '{c.Field}' values must be strings.");
                keys.Add(v.GetString()!);
            }
            var inList = Expression.Call(Expression.Constant(keys), ListContains<string>(), body);
            return c.Op == FilterOperator.In
                ? (Lambda(inList, p), null)
                : (Lambda(Expression.AndAlso(notNull, Expression.Not(inList)), p), null);
        }

        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        var raw = sv.GetString()!;

        if (c.Op == FilterOperator.Eq)   // jiraKey Eq
            return (Lambda(Expression.Equal(body, Expression.Constant(raw, typeof(string))), p), null);

        // Contains / NotContains / StartsWith -> EF.Functions.ILike(body, pattern, "\\") with wildcards escaped.
        var escaped = raw.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = c.Op == FilterOperator.StartsWith ? escaped + "%" : "%" + escaped + "%";
        var ilike = Expression.Call(ILikeMethod,
            Expression.Constant(EF.Functions),
            body,
            Expression.Constant(pattern, typeof(string)),
            Expression.Constant("\\", typeof(string)));
        // A NULL summary never matches, not even "does not contain": body != null && [NOT] ILike(...)
        Expression match = c.Op == FilterOperator.NotContains ? Expression.Not(ilike) : ilike;
        return (Lambda(Expression.AndAlso(notNull, match), p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildParent(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(body, NullString), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        return (Lambda(Expression.Equal(body, Expression.Constant(sv.GetString(), typeof(string))), p), null);
    }

    // ---- helpers ----

    private static readonly ConstantExpression NullGuid = Expression.Constant(null, typeof(Guid?));
    private static readonly ConstantExpression NullDecimal = Expression.Constant(null, typeof(decimal?));
    private static readonly ConstantExpression NullString = Expression.Constant(null, typeof(string));

    private static readonly System.Reflection.MethodInfo ILikeMethod =
        typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })!;

    private static System.Reflection.MethodInfo ListContains<T>() =>
        typeof(List<T>).GetMethod(nameof(List<T>.Contains), new[] { typeof(T) })!;

    private static Expression<Func<Issue, bool>> Lambda(Expression body, ParameterExpression p) =>
        Expression.Lambda<Func<Issue, bool>>(body, p);

    private static Expression<Func<Issue, bool>> OrElse(
        Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b) => Combine(a, b, Expression.OrElse);

    private static Expression<Func<Issue, bool>> AndAlso(
        Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b) => Combine(a, b, Expression.AndAlso);

    /// <summary>Rebinds b's parameter onto a's before joining, so the result is a single lambda.</summary>
    private static Expression<Func<Issue, bool>> Combine(
        Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var p = a.Parameters[0];
        var right = ReplaceParameter.Replace(b.Body, b.Parameters[0], p);
        return Expression.Lambda<Func<Issue, bool>>(join(a.Body, right), p);
    }

    private sealed class ReplaceParameter : ExpressionVisitor
    {
        private readonly ParameterExpression _from, _to;
        private ReplaceParameter(ParameterExpression from, ParameterExpression to) { _from = from; _to = to; }
        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
        public static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to) =>
            new ReplaceParameter(from, to).Visit(body)!;
    }
}

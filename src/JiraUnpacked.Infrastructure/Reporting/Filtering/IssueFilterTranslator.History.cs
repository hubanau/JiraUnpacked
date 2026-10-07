using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The `history` pseudo-field: what happened to an issue's fields, from IssueChangeHistory.
/// Every operator is a correlated subquery over the source the compiler passes in — the injected
/// AppDbContext's set, so the tenant query filter applies inside the subquery as well. History
/// is never read into memory, and nothing a caller sends reaches SQL text: the field name,
/// values, bounds and counts are all parameters.
/// </summary>
public sealed partial class IssueFilterTranslator
{
    /// <summary>Each history condition is a correlated subquery over the largest table, so they are capped on their own.</summary>
    public const int MaxHistoryConditions = 5;
    internal const int MaxHistoryFieldLength = 100;
    internal const int MaxHistoryValueLength = 255;

    internal const int MaxChangeCount = 10_000;

    private static readonly string[] Compares = { "Eq", "Neq", "Gt", "Gte", "Lt", "Lte" };

    /// <summary>
    /// What joins the names of a multi-value field in the changelog. Jira writes most of them with
    /// ", " ("PLAT 100, PLAT 101") but the labels with a space ("DataOps ansible tested"; a label
    /// cannot hold one). Field names are exact, so only "labels" itself is space-separated.
    /// </summary>
    internal static string ListSeparatorFor(string historyField) =>
        historyField == "labels" ? " " : ", ";

    /// <summary>The keys each history operator takes, in their canonical order. Anything else is refused.</summary>
    private static readonly IReadOnlyDictionary<FilterOperator, string[]> HistoryKeys =
        new Dictionary<FilterOperator, string[]>
        {
            [FilterOperator.Was] = new[] { "historyField", "values" },
            [FilterOperator.Changed] = new[] { "historyField", "from", "to", "by", "during" },
            [FilterOperator.ChangeCount] = new[] { "historyField", "compare", "count", "during" },
        };

    /// <summary>
    /// The changelog names (as Jira reports them) of the fields the issue table also holds, and
    /// the dictionary field each resolves through. For these, Was also matches the CURRENT value:
    /// an issue that never changed its status still "was" that status.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CurrentValueFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "status",
            ["assignee"] = "assignee",
            ["priority"] = "priority",
            ["issuetype"] = "issueType",
            ["Sprint"] = "sprint",
        };

    private static bool IsHistory(FilterCondition c) =>
        FilterFields.All.TryGetValue(c.Field, out var f) && f.Type == FieldType.History;

    /// <summary>
    /// What every history operator shares: the value, the source, both lambda parameters (the
    /// issue, a changelog row), <see cref="Rows"/> — "this row is this issue's, for this field" — and
    /// the <see cref="Separator"/> that field's value lists are joined with.
    /// </summary>
    private sealed record HistoryScope(
        JsonElement Value, IQueryable<IssueChangeHistory> Source,
        ParameterExpression Issue, ParameterExpression Row, Expression Rows, string Separator);

    /// <summary>A value list: names (matched whole, or as one element of the field's joined list) and whether (Empty) was asked for.</summary>
    private sealed record HistoryValues(List<string> Names, bool WantsEmpty);

    private async Task<(Expression<Func<Issue, bool>>?, string?)> BuildHistoryAsync(
        FilterCondition c, DictNameResolver resolveDict, IQueryable<IssueChangeHistory>? history, CancellationToken ct)
    {
        if (ReadHistoryScope(c, history, out var scope) is { } error) return (null, error);
        return c.Op switch
        {
            FilterOperator.Was => await BuildWasAsync(scope!, resolveDict, ct),
            FilterOperator.Changed => BuildChanged(scope!),
            FilterOperator.ChangeCount => BuildChangeCount(scope!),
            _ => (null, $"Operator '{c.Op}' is not allowed for field 'history'."),
        };
    }

    /// <summary>The value's shape and its historyField, and the correlation every operator starts from.</summary>
    private static string? ReadHistoryScope(
        FilterCondition c, IQueryable<IssueChangeHistory>? history, out HistoryScope? scope)
    {
        scope = null;
        // The compiler always passes the source; only a caller that has none lands here.
        if (history is null) return "History conditions are not available here.";
        if (c.Value is not { ValueKind: JsonValueKind.Object } v)
            return $"'{c.Op}' on 'history' requires 'value': {{\"historyField\": \"status\", …}}.";

        if (!HistoryKeys.TryGetValue(c.Op, out var keys))
            return $"Operator '{c.Op}' is not allowed for field 'history'.";
        foreach (var prop in v.EnumerateObject())
        {
            // An explicit null is the same as leaving the key out.
            if (!keys.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Null)
                return $"'{prop.Name}' is not part of '{c.Op}'. It takes: {string.Join(", ", keys)}.";
        }

        if (!v.TryGetProperty("historyField", out var hf) || hf.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(hf.GetString()) || hf.GetString()!.Length > MaxHistoryFieldLength)
            return $"'{c.Op}' needs 'historyField': the field's name as Jira reports it (\"status\", \"Sprint\"), at most {MaxHistoryFieldLength} characters.";

        var issue = Expression.Parameter(typeof(Issue), "i");
        var row = Expression.Parameter(typeof(IssueChangeHistory), "h");
        // h.JiraProjectId == i.JiraProjectId && h.JiraIssueId == i.JiraIssueId && h.Field == @field.
        // The field is matched exactly, as Jira names it: "Sprint" and "sprint" are different fields.
        var rows = Expression.AndAlso(
            Expression.AndAlso(
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraProjectId)), Member(issue, nameof(Issue.JiraProjectId))),
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraIssueId)), Member(issue, nameof(Issue.JiraIssueId)))),
            Expression.Equal(Member(row, nameof(IssueChangeHistory.Field)), Parameter(hf.GetString()!)));
        scope = new HistoryScope(v, history, issue, row, rows, ListSeparatorFor(hf.GetString()!));
        return null;
    }

    /// <summary>
    /// Was: some row for the field has one of the values as its FromValue or ToValue — or, for the
    /// five fields the issue table holds, the issue has it now.
    /// </summary>
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildWasAsync(
        HistoryScope s, DictNameResolver resolveDict, CancellationToken ct)
    {
        if (ReadHistoryValues(s.Value, "values", allowEmpty: true, out var values) is { } error) return (null, error);
        if (values is null) return (null, "'Was' needs 'values': at least one value, or null for (Empty).");

        var inHistory = AnyRow(s, Expression.AndAlso(s.Rows, Expression.OrElse(
            MatchValue(Member(s.Row, nameof(IssueChangeHistory.FromValue)), values, s.Separator),
            MatchValue(Member(s.Row, nameof(IssueChangeHistory.ToValue)), values, s.Separator))));

        var historyField = s.Value.GetProperty("historyField").GetString()!;
        if (!CurrentValueFields.TryGetValue(historyField, out var dictField))
            return (Lambda(inHistory, s.Issue), null);

        var now = await CurrentValueAsync(dictField, values, s.Issue, resolveDict, ct);
        return (Lambda(Expression.OrElse(now, inHistory), s.Issue), null);
    }

    /// <summary>
    /// The issue's current value, resolved the way the dictionary filters resolve it: names → ids
    /// through the same tenant-scoped resolver → FK in set (ids sent as one parameter); (Empty) →
    /// no value now.
    /// </summary>
    private static async Task<Expression> CurrentValueAsync(
        string dictField, HistoryValues values, ParameterExpression issue, DictNameResolver resolveDict, CancellationToken ct)
    {
        var selector = (Expression<Func<Issue, Guid?>>)FilterFields.All[dictField].Selector;
        var fk = ReplaceParameter.Replace(selector.Body, selector.Parameters[0], issue);   // i.StatusId …
        Expression? test = values.WantsEmpty ? Expression.Equal(fk, NullGuid) : null;
        if (values.Names.Count > 0)
        {
            var ids = (await resolveDict(dictField, values.Names, ct)).ToList();
            // i.Fk.HasValue && @ids.Contains(i.Fk.Value)
            var inSet = Expression.AndAlso(
                Expression.Property(fk, nameof(Nullable<Guid>.HasValue)),
                Expression.Call(Parameter(ids), ListContains<Guid>(), Expression.Property(fk, nameof(Nullable<Guid>.Value))));
            test = test is null ? inSet : Expression.OrElse(test, inSet);
        }
        return test!;   // ReadHistoryValues never yields an empty list
    }

    /// <summary>Changed: some row for the field satisfies every part given — from, to, by, during. No parts: changed at all.</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildChanged(HistoryScope s)
    {
        if (ReadHistoryValues(s.Value, "from", allowEmpty: true, out var from) is { } e1) return (null, e1);
        if (ReadHistoryValues(s.Value, "to", allowEmpty: true, out var to) is { } e2) return (null, e2);
        if (ReadHistoryValues(s.Value, "by", allowEmpty: false, out var by) is { } e3) return (null, e3);
        if (ReadDuring(s.Value, out var window) is { } e4) return (null, e4);

        var test = s.Rows;
        if (from is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.FromValue)), from, s.Separator));
        if (to is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.ToValue)), to, s.Separator));
        // Authors exactly, as Jira shows them. A row with no author matches no name.
        if (by is not null)
            test = Expression.AndAlso(test, Expression.Call(
                Parameter(by.Names), ListContains<string>(), Member(s.Row, nameof(IssueChangeHistory.Author))));
        if (window is not null && WindowTest(Member(s.Row, nameof(IssueChangeHistory.ChangedAt)), window) is { } inWindow)
            test = Expression.AndAlso(test, inWindow);
        return (Lambda(AnyRow(s, test), s.Issue), null);
    }

    /// <summary>
    /// ChangeCount: how many rows the field has (inside the window, if given), compared to
    /// `count`. An issue with no rows counts 0, so "= 0" finds the never-changed ones.
    /// </summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildChangeCount(HistoryScope s)
    {
        if (!s.Value.TryGetProperty("compare", out var cmp) || cmp.ValueKind != JsonValueKind.String
            || !Compares.Contains(cmp.GetString()))
            return (null, $"'compare' must be one of: {string.Join(", ", Compares)}.");
        if (!s.Value.TryGetProperty("count", out var n) || n.ValueKind != JsonValueKind.Number
            || !n.TryGetInt32(out var count) || count < 0 || count > MaxChangeCount)
            return (null, $"'count' must be a whole number from 0 to {MaxChangeCount}.");
        if (ReadDuring(s.Value, out var window) is { } error) return (null, error);

        var test = s.Rows;
        if (window is not null && WindowTest(Member(s.Row, nameof(IssueChangeHistory.ChangedAt)), window) is { } inWindow)
            test = Expression.AndAlso(test, inWindow);
        var changes = Expression.Call(QueryableCount, Source(s.Source),
            Expression.Quote(Expression.Lambda<Func<IssueChangeHistory, bool>>(test, s.Row)));

        var k = Parameter(count);
        Expression body = cmp.GetString() switch
        {
            "Eq" => Expression.Equal(changes, k),
            "Neq" => Expression.NotEqual(changes, k),
            "Gt" => Expression.GreaterThan(changes, k),
            "Gte" => Expression.GreaterThanOrEqual(changes, k),
            "Lt" => Expression.LessThan(changes, k),
            _ => Expression.LessThanOrEqual(changes, k),   // Lte
        };
        return (Lambda(body, s.Issue), null);
    }

    /// <summary>A resolved `during`: ChangedAt ≥ From (if any), and &lt; To (exclusive) or ≤ To (if any).</summary>
    private sealed record Window(DateTime? From, DateTime? To, bool ToExclusive);

    private const string WindowShape =
        "'during' must look like {\"kind\": \"inLast\", \"amount\": 30, \"unit\": \"days\"}, "
        + "{\"kind\": \"inPeriod\", \"period\": \"lastMonth\", \"timeZone\": \"Europe/Warsaw\"} "
        + "or {\"kind\": \"between\", \"from\": \"2026-09-01\", \"to\": \"2026-09-30\"}.";

    /// <summary>`during`, if given: absent or null means no window.</summary>
    private string? ReadDuring(JsonElement v, out Window? window)
    {
        window = null;
        return v.TryGetProperty("during", out var d) && d.ValueKind != JsonValueKind.Null
            ? ReadWindow(d, out window)
            : null;
    }

    /// <summary>The three window kinds, read by the same rules as InLast, InPeriod and date Between.</summary>
    private string? ReadWindow(JsonElement v, out Window? window)
    {
        window = null;
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            return WindowShape;
        switch (k.GetString())
        {
            case "inLast":
                if (ReadSince(v, WindowShape, out var since) is { } e1) return e1;
                window = new Window(since, null, false);
                return null;
            case "inPeriod":
                if (ReadPeriod(v, WindowShape, out var start, out var end) is { } e2) return e2;
                window = new Window(start, end, true);
                return null;
            case "between":
                JsonElement? fromEl = v.TryGetProperty("from", out var f) && f.ValueKind != JsonValueKind.Null ? f : null;
                JsonElement? toEl = v.TryGetProperty("to", out var t) && t.ValueKind != JsonValueKind.Null ? t : null;
                if (fromEl is null && toEl is null)
                    return "'during' between needs at least one bound; both are empty.";
                if (ReadDateRange(fromEl, toEl, "'during' between", "'from' and 'to'",
                        out var lo, out var hi, out var dateOnly) is { } e3)
                    return e3;
                // A date-only end is "through that day": exclusive at the next midnight.
                window = new Window(lo, dateOnly && hi is { } day ? day.AddDays(1) : hi, dateOnly);
                return null;
            default:
                return $"Unknown 'during' kind '{k.GetString()}'. Use one of: inLast, inPeriod, between.";
        }
    }

    /// <summary>
    /// ChangedAt inside the window. Bounds are parameters, so a moving "last 30 days" never churns
    /// EF's query cache. Null when the window bounds nothing.
    /// </summary>
    private static Expression? WindowTest(Expression changedAt, Window w)
    {
        Expression? test = null;
        if (w.From is { } lo) test = Expression.GreaterThanOrEqual(changedAt, Parameter(lo));
        if (w.To is { } hi)
            test = And(test, w.ToExclusive
                ? Expression.LessThan(changedAt, Parameter(hi))
                : Expression.LessThanOrEqual(changedAt, Parameter(hi)));
        return test;
    }

    /// <summary>
    /// An optional list under <paramref name="key"/>: absent or null → no list, no error. Strings of
    /// at most 255 characters; null or "" is (Empty) where allowed (Jira stores "no value" as
    /// either). At most 100 entries, and never empty — leaving the key out is how a caller says "any".
    /// </summary>
    private static string? ReadHistoryValues(JsonElement v, string key, bool allowEmpty, out HistoryValues? list)
    {
        list = null;
        if (!v.TryGetProperty(key, out var arr) || arr.ValueKind == JsonValueKind.Null) return null;
        var what = allowEmpty ? "strings, or null for (Empty)" : "author names";
        if (arr.ValueKind != JsonValueKind.Array) return $"'{key}' must be a list of {what}.";
        var count = arr.GetArrayLength();
        if (count == 0) return $"'{key}' needs at least one value; leave it out to accept any.";
        if (count > MaxInValues) return $"'{key}' accepts at most {MaxInValues} values.";

        var names = new List<string>();
        var wantsEmpty = false;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return $"'{key}' must be a list of {what}.";
            var s = e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            if (s.Length == 0)
            {
                if (!allowEmpty) return $"'{key}' takes author names; (Empty) is not one.";
                wantsEmpty = true;
                continue;
            }
            if (s.Length > MaxHistoryValueLength)
                return $"Values in '{key}' may be at most {MaxHistoryValueLength} characters.";
            if (!names.Contains(s)) names.Add(s);
        }
        list = new HistoryValues(names, wantsEmpty);
        return null;
    }

    /// <summary>
    /// The column holds one of the names — equal to it, or one ELEMENT of the field's joined list
    /// (<paramref name="separator"/>: "v…", "…v", "…v…"), so "PLAT 10" never matches "PLAT 101" — or, for (Empty), holds
    /// nothing. Built from ==, StartsWith, EndsWith and Contains on parameters, which EF/Npgsql
    /// translate with the pattern escaped: a "%" or "_" in a name is a literal.
    /// </summary>
    private static Expression MatchValue(Expression column, HistoryValues values, string separator)
    {
        Expression? match = values.WantsEmpty
            ? Expression.OrElse(Expression.Equal(column, NullString), Expression.Equal(column, Expression.Constant("")))
            : null;
        if (values.Names.Count > 0)
        {
            Expression element = Expression.Call(Parameter(values.Names), ListContains<string>(), column);
            foreach (var name in values.Names)
            {
                element = Expression.OrElse(element, Expression.Call(column, StringStartsWith, Parameter(name + separator)));
                element = Expression.OrElse(element, Expression.Call(column, StringEndsWith, Parameter(separator + name)));
                element = Expression.OrElse(element, Expression.Call(column, StringContains, Parameter(separator + name + separator)));
            }
            // NULL holds no name. The guard keeps .StartsWith off a null in memory; in SQL it costs nothing.
            var named = Expression.AndAlso(Expression.NotEqual(column, NullString), element);
            match = match is null ? named : Expression.OrElse(match, named);
        }
        return match!;   // ReadHistoryValues never yields an empty list
    }

    /// <summary>EXISTS (rows of the source satisfying <paramref name="test"/>).</summary>
    private static Expression AnyRow(HistoryScope s, Expression test) =>
        Expression.Call(QueryableAny, Source(s.Source),
            Expression.Quote(Expression.Lambda<Func<IssueChangeHistory, bool>>(test, s.Row)));

    /// <summary>
    /// The source as a captured value — the very expression a C# closure over
    /// `_db.IssueChangeHistory` produces. EF evaluates it and inlines the set's query root, tenant
    /// filter included, exactly as it does for `_db.Statuses` in IssueQueryService's projection;
    /// in memory it is just the sequence.
    /// </summary>
    private static Expression Source(IQueryable<IssueChangeHistory> history) => Parameter(history);

    private static MemberExpression Member(Expression on, string name) => Expression.Property(on, name);

    private static readonly MethodInfo QueryableCount = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Count) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));

    private static readonly MethodInfo QueryableAny = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));

    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;
}

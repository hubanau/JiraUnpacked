using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

public sealed record FilterBuildResult(Expression<Func<Issue, bool>>? Predicate, string? Error);

/// <summary>Resolves a dict field's display names to surrogate Ids (tenant-scoped, injected by the service).</summary>
public delegate Task<IReadOnlyList<Guid>> DictNameResolver(
    string field, IReadOnlyList<string> names, CancellationToken ct);

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

    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildConditionAsync(
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

        // Names to resolve, and whether (Empty) — a JSON null inside 'values' — was asked for.
        List<string> names;
        var wantsEmpty = false;
        if (c.Op is FilterOperator.In or FilterOperator.NotIn)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            names = new List<string>();
            foreach (var v in c.Values)
            {
                if (v.ValueKind == JsonValueKind.Null) { wantsEmpty = true; continue; }
                if (v.ValueKind != JsonValueKind.String)
                    return (null, $"Field '{c.Field}' values must be strings, or null for (Empty).");
                names.Add(v.GetString()!);
            }
        }
        else // Eq / Neq
        {
            if (c.Value is not { ValueKind: JsonValueKind.String } sv)
                return (null, $"Field '{c.Field}' requires a string 'value'.");
            names = new List<string> { sv.GetString()! };
        }

        // In [null] is IsNull by another name: nothing to resolve, so no round trip.
        var ids = names.Count == 0 ? new List<Guid>() : (await resolveDict(field.Name, names, ct)).ToList();

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
            FilterOperator.In when wantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimal(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, decimal?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        var notNull = Expression.NotEqual(body, NullDecimal);
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullDecimal), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);
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

    private static (Expression<Func<Issue, bool>>?, string?) BuildDate(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, DateTime>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
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

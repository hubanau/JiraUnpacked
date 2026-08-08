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
    Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default);
}

public sealed class IssueFilterTranslator : IIssueFilterTranslator
{
    private const int MaxConditions = 20;
    private const int MaxInValues = 100;

    public async Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default)
    {
        if (conditions.Count > MaxConditions)
            return Err($"A filter may have at most {MaxConditions} conditions.");

        var predicates = new List<Expression<Func<Issue, bool>>>();
        foreach (var c in conditions)
        {
            if (!FilterFields.All.TryGetValue(c.Field, out var field))
                return Err($"Unknown field '{c.Field}'.");
            if (!field.Ops.Contains(c.Op))
                return Err($"Operator '{c.Op}' is not allowed for field '{c.Field}'.");

            var (pred, error) = field.Type switch
            {
                FieldType.DictName  => await BuildDictAsync(field, c, resolveDict, ct),
                FieldType.Decimal   => BuildDecimal(field, c),
                FieldType.DateTime  => BuildDate(field, c),
                FieldType.Text      => BuildText(field, c),
                FieldType.ParentRef => BuildParent(field, c),
                _ => (null, "Unsupported field type."),
            };
            if (error is not null) return Err(error);
            predicates.Add(pred!);
        }

        Expression<Func<Issue, bool>> combined = predicates.Aggregate(
            (Expression<Func<Issue, bool>>)(i => true), AndAlso);
        return new FilterBuildResult(combined, null);
    }

    private static FilterBuildResult Err(string m) => new(null, m);

    // ---- per-type builders ----

    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)

        if (c.Op == FilterOperator.IsNull)
            return (Lambda(Expression.Equal(fkBody, NullGuid), p), null);

        // Gather names for Eq (single) or In (list).
        List<string> names;
        if (c.Op == FilterOperator.In)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with 'In' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'In' accepts at most {MaxInValues} values.");
            names = new List<string>();
            foreach (var v in c.Values)
            {
                if (v.ValueKind != JsonValueKind.String) return (null, $"Field '{c.Field}' values must be strings.");
                names.Add(v.GetString()!);
            }
        }
        else // Eq / Neq
        {
            if (c.Value is not { ValueKind: JsonValueKind.String } sv)
                return (null, $"Field '{c.Field}' requires a string 'value'.");
            names = new List<string> { sv.GetString()! };
        }

        var ids = (await resolveDict(field.Name, names, ct)).ToList();

        // i.Fk.HasValue && ids.Contains(i.Fk.Value)
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        var contains = Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);
        var inSet = Expression.AndAlso(hasValue, contains);

        return c.Op switch
        {
            // Neq includes unassigned: i.Fk == null || !(hasValue && contains)
            FilterOperator.Neq => (Lambda(Expression.OrElse(Expression.Equal(fkBody, NullGuid), Expression.Not(inSet)), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimal(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, decimal?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullDecimal), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.Number } nv || !nv.TryGetDecimal(out var d))
            return (null, $"Field '{c.Field}' requires a numeric 'value'.");
        var k = Expression.Constant((decimal?)d, typeof(decimal?));
        Expression cmp = c.Op switch
        {
            FilterOperator.Eq => Expression.Equal(body, k),
            FilterOperator.Neq => Expression.NotEqual(body, k),
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
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        var raw = sv.GetString()!;

        if (c.Op == FilterOperator.Eq)   // jiraKey Eq
            return (Lambda(Expression.Equal(body, Expression.Constant(raw, typeof(string))), p), null);

        // Contains / StartsWith -> EF.Functions.ILike(body, pattern, "\\") with wildcards escaped.
        var escaped = raw.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = c.Op == FilterOperator.StartsWith ? escaped + "%" : "%" + escaped + "%";
        var ilike = Expression.Call(ILikeMethod,
            Expression.Constant(EF.Functions),
            body,
            Expression.Constant(pattern, typeof(string)),
            Expression.Constant("\\", typeof(string)));
        // Guard null summary: body != null && ILike(...)
        var notNull = Expression.NotEqual(body, NullString);
        return (Lambda(Expression.AndAlso(notNull, ilike), p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildParent(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
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

    private static Expression<Func<Issue, bool>> AndAlso(
        Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b)
    {
        var p = a.Parameters[0];
        var right = ReplaceParameter.Replace(b.Body, b.Parameters[0], p);
        return Expression.Lambda<Func<Issue, bool>>(Expression.AndAlso(a.Body, right), p);
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

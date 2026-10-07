using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>Story points (Between included), text and parent.</summary>
public sealed partial class IssueFilterTranslator
{
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

    private static readonly ConstantExpression NullDecimal = Expression.Constant(null, typeof(decimal?));
    private static readonly ConstantExpression NullString = Expression.Constant(null, typeof(string));

    private static readonly System.Reflection.MethodInfo ILikeMethod =
        typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })!;
}

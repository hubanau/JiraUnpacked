using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>Shared helpers: expression building, and the [from, to] reader both Betweens use.</summary>
public sealed partial class IssueFilterTranslator
{
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
    /// A value EF sends as a query parameter rather than inlining: a member read off a captured
    /// object, which is how EF sees a closure variable.
    /// </summary>
    private static MemberExpression Parameter<T>(T value) =>
        Expression.Field(Expression.Constant(new Captured<T>(value)), nameof(Captured<T>.Value));

    private sealed class Captured<T>
    {
        public readonly T Value;
        public Captured(T value) => Value = value;
    }

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

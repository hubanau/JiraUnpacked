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

    /// <summary>The keys each history operator takes, in their canonical order. Anything else is refused.</summary>
    private static readonly IReadOnlyDictionary<FilterOperator, string[]> HistoryKeys =
        new Dictionary<FilterOperator, string[]>
        {
            [FilterOperator.Changed] = new[] { "historyField" },
        };

    private static bool IsHistory(FilterCondition c) =>
        FilterFields.All.TryGetValue(c.Field, out var f) && f.Type == FieldType.History;

    /// <summary>
    /// What every history operator shares: the value, the source, both lambda parameters (the
    /// issue, a changelog row), and <see cref="Rows"/> — "this row is this issue's, for this field".
    /// </summary>
    private sealed record HistoryScope(
        JsonElement Value, IQueryable<IssueChangeHistory> Source,
        ParameterExpression Issue, ParameterExpression Row, Expression Rows);

    private (Expression<Func<Issue, bool>>?, string?) BuildHistory(
        FilterCondition c, IQueryable<IssueChangeHistory>? history)
    {
        if (ReadHistoryScope(c, history, out var scope) is { } error) return (null, error);
        return c.Op switch
        {
            FilterOperator.Changed => BuildChanged(scope!),
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

        var keys = HistoryKeys[c.Op];
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
        scope = new HistoryScope(v, history, issue, row, rows);
        return null;
    }

    /// <summary>Changed: some row for the field exists.</summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildChanged(HistoryScope s) =>
        (Lambda(AnyRow(s, s.Rows), s.Issue), null);

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

    private static readonly MethodInfo QueryableAny = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));
}

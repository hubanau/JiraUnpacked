using System.Linq.Expressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

internal enum FieldType { DictName, Decimal, DateTime, Text, ParentRef }

/// <summary>A filterable field: its type, its allowed operators, and (for the builder) the member selector.</summary>
internal sealed record FilterField(
    string Name, FieldType Type, IReadOnlySet<FilterOperator> Ops, LambdaExpression Selector);

/// <summary>
/// The closed allow-list. A field name that is not a key here is rejected — this is the injection
/// boundary: no user string ever becomes a reflected member access.
/// </summary>
internal static class FilterFields
{
    private static IReadOnlySet<FilterOperator> Ops(params FilterOperator[] ops) => ops.ToHashSet();

    // Selectors are typed lambdas over Issue; the translator grafts them onto comparisons.
    public static readonly IReadOnlyDictionary<string, FilterField> All =
        new Dictionary<string, FilterField>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"]     = Dict("status",     (Issue i) => i.StatusId),
            ["issueType"]  = Dict("issueType",  (Issue i) => i.IssueTypeId),
            ["priority"]   = Dict("priority",   (Issue i) => i.PriorityId),
            ["assignee"]   = Dict("assignee",   (Issue i) => i.AssigneeId),
            ["sprint"]     = Dict("sprint",     (Issue i) => i.SprintId),
            ["team"]       = Dict("team",       (Issue i) => i.TeamId),
            ["storyPoints"]= new("storyPoints", FieldType.Decimal,
                                 Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Lt, FilterOperator.Lte,
                                     FilterOperator.Gt, FilterOperator.Gte, FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, decimal?>>)(i => i.StoryPoints)),
            ["createdAt"]  = Date("createdAt",  (Issue i) => i.JiraCreatedAt),
            ["updatedAt"]  = Date("updatedAt",  (Issue i) => i.JiraUpdatedAt),
            ["summary"]    = new("summary", FieldType.Text,
                                 Ops(FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
                                     FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, string?>>)(i => i.Summary)),
            ["jiraKey"]    = new("jiraKey", FieldType.Text,
                                 Ops(FilterOperator.Eq, FilterOperator.In, FilterOperator.NotIn,
                                     FilterOperator.Contains, FilterOperator.StartsWith),
                                 (Expression<Func<Issue, string?>>)(i => i.JiraKey)),
            ["parent"]     = new("parent", FieldType.ParentRef,
                                 Ops(FilterOperator.Eq, FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, string?>>)(i => i.ParentJiraIssueId)),
        };

    private static FilterField Dict(string name, Expression<Func<Issue, Guid?>> sel) =>
        new(name, FieldType.DictName,
            Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
                FilterOperator.IsNull, FilterOperator.IsNotNull), sel);

    private static FilterField Date(string name, Expression<Func<Issue, DateTime>> sel) =>
        new(name, FieldType.DateTime,
            Ops(FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte), sel);
}

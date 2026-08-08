using System.Text.Json;

namespace JiraUnpacked.Domain.Abstractions;

public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull }

/// <summary>One filter condition. Value for single-value ops; Values for In. Both null for IsNull.</summary>
public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values);

/// <summary>A flat AND filter over the modeled issue fields. Null/empty Conditions = unfiltered.</summary>
public sealed record IssueFilterRequest(
    List<FilterCondition>? Conditions, int Page = 1, int PageSize = 50);

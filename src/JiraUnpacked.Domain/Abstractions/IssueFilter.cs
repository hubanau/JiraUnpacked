using System.Text.Json;

namespace JiraUnpacked.Domain.Abstractions;

public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull }

/// <summary>How a group joins its parts. Applies to the group's conditions AND its nested groups alike.</summary>
public enum FilterLogic { And, Or }

/// <summary>One filter condition. Value for single-value ops; Values for In. Both null for IsNull.</summary>
public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values);

/// <summary>
/// A boolean group: its conditions and its nested groups are all joined by <see cref="Logic"/>.
/// A group must contain at least one part — an empty And is vacuously true and an empty Or is
/// vacuously false, so an empty group is rejected rather than guessed at.
/// </summary>
public sealed record FilterGroup(
    FilterLogic Logic, List<FilterCondition>? Conditions, List<FilterGroup>? Groups);

/// <summary>
/// Either a flat AND over <see cref="Conditions"/> (the original FR-E4 shape, still the one the
/// SPA sends) or a boolean tree under <see cref="Filter"/>. Both populated is an error, not a
/// merge. Neither populated = unfiltered.
/// </summary>
public sealed record IssueFilterRequest(
    List<FilterCondition>? Conditions, FilterGroup? Filter, int Page = 1, int PageSize = 50);

using System.Text.Json;

namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Serialised and stored BY NAME. New members are appended, never inserted: the ordinal is a
/// history, not a meaning (the catalog's display order lives in FilterCatalogService).
/// </summary>
public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains }

/// <summary>How a group joins its parts. Applies to the group's conditions AND its nested groups alike.</summary>
public enum FilterLogic { And, Or }

/// <summary>
/// One filter condition. Value for single-value ops; Values for In/NotIn, where a JSON null
/// element stands for (Empty). Both null for IsNull/IsNotNull. Disabled = kept in the definition
/// but skipped entirely: not validated, not counted, not applied.
/// </summary>
public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values, bool? Disabled = null);

/// <summary>
/// A boolean group: its conditions and its nested groups are all joined by <see cref="Logic"/>.
/// A group must be WRITTEN with at least one part — an empty And is vacuously true and an empty
/// Or vacuously false, so an empty group is rejected rather than guessed at. Parts switched off
/// with <see cref="Disabled"/> are a different thing: a group whose every part is off is simply
/// absent, and a root with nothing on matches everything.
/// </summary>
public sealed record FilterGroup(
    FilterLogic Logic, List<FilterCondition>? Conditions, List<FilterGroup>? Groups, bool? Disabled = null);

/// <summary>
/// Either a flat AND over <see cref="Conditions"/> (the original FR-E4 shape, still the one the
/// SPA sends) or a boolean tree under <see cref="Filter"/>. Both populated is an error, not a
/// merge. Neither populated = unfiltered.
/// </summary>
/// <param name="SortBy">
/// A column from the closed sort catalog. Null means the default (newest updated first). An
/// unknown name is a 400, never a silent fallback: plausible rows in the wrong order, with
/// nothing on screen saying so, is worse than an error.
/// </param>
public sealed record IssueFilterRequest(
    List<FilterCondition>? Conditions, FilterGroup? Filter, int Page = 1, int PageSize = 50,
    string? SortBy = null, bool SortDescending = true);

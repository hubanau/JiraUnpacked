namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// One saved view: the conditions/filter pair every other endpoint takes, plus the list's
/// presentation. A member the view does not store comes back null — "use the list's default".
/// </summary>
public sealed record SavedFilterView(
    Guid Id, string Name,
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns, string? SortBy, bool? SortDescending,
    bool IsDefault,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Body of the create and update calls. Columns/SortBy/SortDescending are the list's
/// presentation; left out, the view stores nothing for them and the list uses its defaults.
/// IsDefault left out: false on create, unchanged on update.
/// </summary>
public sealed record SaveFilterRequest(
    string? Name, List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns = null, string? SortBy = null, bool? SortDescending = null,
    bool? IsDefault = null)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}

/// <summary>
/// How a save failed. NotFound covers an unknown/cross-tenant project AND an unknown filter id,
/// deliberately: distinguishing them would leak which project ids exist.
/// </summary>
public enum SaveFilterError { None, NotFound, Invalid, Conflict }

public sealed record SaveFilterResult(SavedFilterView? Filter, SaveFilterError Error, string? Message)
{
    public static SaveFilterResult NotFound() => new(null, SaveFilterError.NotFound, null);
    public static SaveFilterResult Invalid(string message) => new(null, SaveFilterError.Invalid, message);
    public static SaveFilterResult Conflict(string message) => new(null, SaveFilterError.Conflict, message);
    public static SaveFilterResult Ok(SavedFilterView filter) => new(filter, SaveFilterError.None, null);
}

/// <summary>
/// Named per-project filter definitions. Storage only: nothing here executes a filter. A client
/// reads a definition and posts it to the query or report endpoints exactly as it posts an
/// ad-hoc one, so there is still exactly one path from filter input to predicate.
/// </summary>
public interface ISavedFilterService
{
    /// <summary>Null when the project is unknown or belongs to another tenant (=> 404).</summary>
    Task<IReadOnlyList<SavedFilterView>?> ListAsync(Guid projectId, CancellationToken ct = default);

    Task<SavedFilterView?> GetAsync(Guid projectId, Guid filterId, CancellationToken ct = default);

    Task<SaveFilterResult> CreateAsync(Guid projectId, SaveFilterRequest request, CancellationToken ct = default);

    Task<SaveFilterResult> UpdateAsync(
        Guid projectId, Guid filterId, SaveFilterRequest request, CancellationToken ct = default);

    /// <summary>False when the project or the filter is unknown/cross-tenant (=> 404).</summary>
    Task<bool> DeleteAsync(Guid projectId, Guid filterId, CancellationToken ct = default);
}

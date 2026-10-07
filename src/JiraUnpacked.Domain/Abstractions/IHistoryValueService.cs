namespace JiraUnpacked.Domain.Abstractions;

/// <summary>A changelog field this project has history for, and how many changelog rows it has.</summary>
public sealed record HistoryFieldCount(string Name, int Count);

/// <summary>Null Fields => the project is unknown or another tenant's => 404.</summary>
public sealed record HistoryFieldsResult(IReadOnlyList<HistoryFieldCount>? Fields);

/// <summary>
/// Values + null Error => 200. Error => 400. Both null => the project is unknown or another
/// tenant's => 404. EmptyCount is the number behind the picker's (Empty) entry.
/// </summary>
public sealed record HistoryValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error);

/// <summary>
/// What the history row's pickers offer: the fields this project's changelog has, and one field's
/// values or authors. Counts are over the whole project's changelog, like the filter-values
/// counts are over the whole project.
/// </summary>
public interface IHistoryValueService
{
    Task<HistoryFieldsResult> GetFieldsAsync(Guid projectId, CancellationToken ct = default);

    /// <param name="kind">"value" (From and To, lists split on the field's list separator) or "author".</param>
    Task<HistoryValuesResult> GetValuesAsync(Guid projectId, string? field, string? kind, CancellationToken ct = default);
}

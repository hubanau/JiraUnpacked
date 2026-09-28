namespace JiraUnpacked.Domain.Abstractions;

/// <summary>One pickable value and how many of this project's issues carry it.</summary>
public sealed record FilterValueCount(string Name, int Count);

/// <summary>A smart sprint value (see the design spec §2) and how many of this project's issues it matches now.</summary>
public sealed record SmartValueCount(string Key, string Label, int Count);

/// <summary>
/// Values + null Error => 200. Null Values + Error => 400. Both null => the project is unknown
/// or belongs to another tenant => 404. EmptyCount is how many of the project's issues have no
/// value for the field — the number behind the picker's (Empty) entry. Smart is set for sprint
/// only.
/// </summary>
public sealed record FilterValuesResult(
    IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error,
    IReadOnlyList<SmartValueCount>? Smart = null);

/// <summary>
/// The values a dictionary field actually takes in one project, with counts, for the filter's
/// value picker. Counts are over the whole project, not the current filter: cheap, stable while
/// the user edits, and honest about what exists.
/// </summary>
public interface IFilterValueService
{
    Task<FilterValuesResult> GetAsync(Guid projectId, string field, CancellationToken ct = default);
}

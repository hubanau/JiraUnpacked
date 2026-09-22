namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Values + null Error => 200. Null Values + Error => 400. Both null => the project is unknown
/// or belongs to another tenant => 404.
/// </summary>
public sealed record FilterValuesResult(IReadOnlyList<string>? Values, string? Error);

/// <summary>
/// The values a dictionary field actually takes in one project, for the filter's value picker.
/// </summary>
public interface IFilterValueService
{
    Task<FilterValuesResult> GetAsync(Guid projectId, string field, CancellationToken ct = default);
}

// src/JiraUnpacked.Domain/Abstractions/IFieldChurnQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// One issue whose chosen field kept changing. <c>Changes</c> counts every changelog entry for
/// the field, including removals; <c>DistinctValues</c> counts how many different values it ever
/// held. They differ when an issue is moved out of a sprint and back into the same one, and the
/// pair is what separates "dragged through five sprints" from "toggled in and out of one".
/// </summary>
public sealed record FieldChurnItem(
    string JiraIssueId, string JiraKey, string? Summary, string? IssueType,
    string? CurrentStatus, string Class, int Changes, int DistinctValues,
    DateTime FirstChangedAt, DateTime LastChangedAt);

public sealed record FieldChurnBand(string Band, int Count);

/// <summary>
/// <c>AvailableFields</c> is every field this project's changelog actually contains, so the
/// caller can offer a real choice instead of a hardcoded list — the changelog carries whatever
/// Jira reports, which differs per site and per custom field.
/// </summary>
public sealed record FieldChurnReport(
    string Field, IReadOnlyList<string> AvailableFields,
    PagedResult<FieldChurnItem> Items, IReadOnlyList<FieldChurnBand> Histogram);

/// <summary>
/// "Which issues keep having this field changed?" — the question JQL cannot ask. Jira has a
/// CHANGED predicate but no way to COUNT changes, so this reads the ingested changelog.
///
/// Returns a null Report with a null Error for an unknown or other-tenant project (=> 404, no
/// existence leak) and a non-null Error for a rejected filter or status class (=> 400), matching
/// <see cref="IMetricsQueryService"/>.
/// </summary>
public interface IFieldChurnQueryService
{
    /// <param name="field">Changelog field name; null or blank means Sprint.</param>
    /// <param name="minChanges">Lower bound on the change count. Below 1 is clamped to 1.</param>
    /// <param name="statusClasses">
    /// Current status classes to keep. Null or empty means Active only — the default because the
    /// question is normally about work still in flight, not about what already shipped.
    /// </param>
    Task<ReportResult<FieldChurnReport>> GetFieldChurnAsync(
        Guid projectId, string? field, int minChanges, IReadOnlyList<string>? statusClasses,
        int page, int pageSize, ReportFilter? filter = null, CancellationToken ct = default);
}

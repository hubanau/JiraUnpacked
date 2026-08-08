// src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Null Result + null Error => project unknown/cross-tenant (404). Non-null Error => bad filter (400).
/// Otherwise Result is the filtered page.
/// </summary>
public sealed record IssueQueryResult(PagedResult<IssueListItem>? Result, string? Error);

public interface IIssueQueryService
{
    /// <summary>Paginated issues for a project, tenant-scoped. Null when the project is not the caller's.</summary>
    Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>One issue's detail (with change history + parent/children). Null when not the caller's.</summary>
    Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default);

    /// <summary>
    /// Null Result + null Error => project unknown/cross-tenant (404). Non-null Error => bad filter (400).
    /// Otherwise Result is the filtered page.
    /// </summary>
    Task<IssueQueryResult> QueryAsync(Guid projectId, IssueFilterRequest request, CancellationToken ct = default);
}

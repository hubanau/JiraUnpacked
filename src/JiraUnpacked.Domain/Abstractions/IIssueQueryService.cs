// src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

public interface IIssueQueryService
{
    /// <summary>Paginated issues for a project, tenant-scoped. Null when the project is not the caller's.</summary>
    Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>One issue's detail (with change history + parent/children). Null when not the caller's.</summary>
    Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default);
}

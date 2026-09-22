// src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Null Result + null Error => project unknown/cross-tenant (404). Non-null Error => bad filter (400).
/// Otherwise Result is the filtered page.
/// </summary>
public sealed record IssueQueryResult(PagedResult<IssueListItem>? Result, string? Error);

/// <summary>
/// What an export would produce, without producing it. Null ProjectKey + null Error => the
/// project is not the caller's => 404. Non-null Error => 400. Otherwise the export may proceed.
/// </summary>
public sealed record IssueExportInfo(string? ProjectKey, int Count, string? Error);

public interface IIssueQueryService
{
    /// <summary>Paginated issues for a project, tenant-scoped. Null when the project is not the caller's.</summary>
    Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Validates an export and reports how big it would be, so the caller can refuse an
    /// oversized one BEFORE any bytes are written — once the response has started there is no
    /// way left to say no.
    /// </summary>
    Task<IssueExportInfo> PrepareExportAsync(Guid projectId, IssueFilterRequest request, CancellationToken ct = default);

    /// <summary>
    /// The filtered, sorted set as a stream. No paging and no ToListAsync: the rows are written
    /// to the response as they arrive, because materialising 92,000 of them to build a string is
    /// the mistake this project spent a week removing from derivation.
    ///
    /// Assumes PrepareExportAsync already returned success; an async stream has nowhere to put
    /// an error once enumeration has begun.
    /// </summary>
    IAsyncEnumerable<IssueListItem> StreamExportAsync(Guid projectId, IssueFilterRequest request, CancellationToken ct = default);

    /// <summary>One issue's detail (with change history + parent/children). Null when not the caller's.</summary>
    Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default);

    /// <summary>
    /// Null Result + null Error => project unknown/cross-tenant (404). Non-null Error => bad filter (400).
    /// Otherwise Result is the filtered page.
    /// </summary>
    Task<IssueQueryResult> QueryAsync(Guid projectId, IssueFilterRequest request, CancellationToken ct = default);
}

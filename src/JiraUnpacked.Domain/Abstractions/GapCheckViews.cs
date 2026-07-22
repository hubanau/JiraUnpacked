// src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs
namespace JiraUnpacked.Domain.Abstractions;

public sealed record GapCheckInfo(string Id, string Name, string Description);

/// <summary>Ok = the check ran. NotConfigured = it could not run (no issue type classified Epic),
/// which must never be confused with "no problems found".</summary>
public enum GapCheckStatus { Ok, NotConfigured }

public sealed record GapCheckChild(
    string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);

public sealed record GapCheckViolation(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string Class, string? IssueType,
    IReadOnlyList<GapCheckChild> Children);

public sealed record GapCheckResult(
    GapCheckInfo Check, string Status, PagedResult<GapCheckViolation> Violations);

public interface IGapCheckService
{
    /// <summary>Null when the project is unknown or belongs to another tenant (=> 404).</summary>
    Task<IReadOnlyList<GapCheckInfo>?> GetCatalogAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Null when the project is unknown/cross-tenant OR the checkId is unknown (=> 404).</summary>
    Task<GapCheckResult?> RunAsync(
        Guid projectId, string checkId, int page, int pageSize, CancellationToken ct = default);
}

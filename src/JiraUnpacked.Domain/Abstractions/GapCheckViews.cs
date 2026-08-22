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

    /// <summary>
    /// Null Report + null Error when the project is unknown/cross-tenant OR the checkId is
    /// unknown (=> 404); non-null Error for a rejected filter (=> 400).
    ///
    /// A non-null <c>filter</c> selects which issues are EXAMINED. It never changes the evidence
    /// used to judge them: the context is always built from the whole project, so an epic judged
    /// on a filtered subject still sees every one of its children.
    /// </summary>
    Task<ReportResult<GapCheckResult>> RunAsync(
        Guid projectId, string checkId, int page, int pageSize,
        ReportFilter? filter = null, CancellationToken ct = default);
}

// src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record IssueTypeClassInfo(string JiraIssueTypeId, string Name, string Class);

public sealed record IssueTypeClassInput(string JiraIssueTypeId, IssueTypeClass Class);

public enum SetIssueTypeClassesOutcome { Ok, ConnectionNotFound, UnknownIssueTypeId }

public sealed record SetIssueTypeClassesResult(
    SetIssueTypeClassesOutcome Outcome, string? UnknownJiraIssueTypeId);

/// <summary>
/// Reads and sets the per-connection issue-type classification used to identify epics.
/// Connection-scoped because IssueTypes are keyed by JiraConnectionId and shared across
/// every project on that connection.
/// </summary>
public interface IIssueTypeClassService
{
    /// <summary>Null when the connection is unknown or belongs to another tenant (=> 404).</summary>
    Task<IReadOnlyList<IssueTypeClassInfo>?> GetAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Additive: only the listed types change; unlisted types keep their current Class.</summary>
    Task<SetIssueTypeClassesResult> SetAsync(
        Guid connectionId, IReadOnlyList<IssueTypeClassInput> items, CancellationToken ct = default);
}

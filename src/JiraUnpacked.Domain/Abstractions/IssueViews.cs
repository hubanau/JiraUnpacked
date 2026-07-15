// src/JiraUnpacked.Domain/Abstractions/IssueViews.cs
namespace JiraUnpacked.Domain.Abstractions;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record IssueListItem(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string? IssueType, string? Priority, string? Assignee, string? Sprint, string? Team,
    decimal? StoryPoints, string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt);

public sealed record IssueRef(string JiraIssueId, string JiraKey, string? Summary);

public sealed record ChangeHistoryEntry(string Field, string? FromValue, string? ToValue, string? Author, DateTime ChangedAt);

public sealed record IssueDetail(
    IssueListItem Issue, IssueRef? Parent, IReadOnlyList<IssueRef> Children, IReadOnlyList<ChangeHistoryEntry> ChangeHistory);

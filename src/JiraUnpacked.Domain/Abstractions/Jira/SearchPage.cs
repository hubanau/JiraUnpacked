namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record RawIssue(string JiraIssueId, string RawJson);
public sealed record SearchPage(IReadOnlyList<RawIssue> Issues, string? NextPageToken);

/// <summary>One page of issue ids only — the cheap listing used to reconcile a scope.</summary>
public sealed record IssueIdPage(IReadOnlyList<string> JiraIssueIds, string? NextPageToken);

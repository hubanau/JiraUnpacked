namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record RawIssue(string JiraIssueId, string RawJson);
public sealed record SearchPage(IReadOnlyList<RawIssue> Issues, string? NextPageToken);

namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record ChangelogPage(IReadOnlyList<string> ValuesJson, int Total, bool IsLast);

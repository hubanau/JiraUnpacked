namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record JiraField(string Id, string Name, bool Custom);
public sealed record JiraProjectSummary(string Id, string Key, string Name);
public sealed record JiraStatus(string Id, string Name);
public sealed record JqlValidationResult(bool Valid, IReadOnlyList<string> Errors);

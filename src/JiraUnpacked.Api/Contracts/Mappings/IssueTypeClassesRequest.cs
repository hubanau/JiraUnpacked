using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record IssueTypeClassItem(string JiraIssueTypeId, IssueTypeClass Class);
public sealed record IssueTypeClassesRequest(IReadOnlyList<IssueTypeClassItem> IssueTypes);

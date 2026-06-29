using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record StatusSemanticItem(string JiraStatusId, string StatusName, StatusClass Class);
public sealed record StatusSemanticsRequest(IReadOnlyList<StatusSemanticItem> Statuses);

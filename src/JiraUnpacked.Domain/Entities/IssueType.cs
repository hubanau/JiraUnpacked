using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class IssueType : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraIssueTypeId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public IssueTypeClass Class { get; set; }
}

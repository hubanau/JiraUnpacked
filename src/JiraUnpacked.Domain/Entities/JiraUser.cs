using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class JiraUser : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraAccountId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}

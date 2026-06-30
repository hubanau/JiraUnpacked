using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Status : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraStatusId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? StatusCategory { get; set; }
}

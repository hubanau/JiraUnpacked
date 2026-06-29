using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;

public sealed class JiraQuery : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string Jql { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}

using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class RawIssueSnapshot : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string Payload { get; set; } = default!; // jsonb
    public DateTime FetchedAt { get; set; }
}

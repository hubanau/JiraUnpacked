using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;

public sealed class JiraProject : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraProjectKey { get; set; } = default!;
    public string JiraProjectId { get; set; } = default!; // Jira's stable id
    public int SyncIntervalMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
}

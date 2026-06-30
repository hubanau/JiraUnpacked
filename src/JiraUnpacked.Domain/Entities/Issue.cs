using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Issue : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string JiraKey { get; set; } = default!;
    public Guid? IssueTypeId { get; set; }
    public Guid? StatusId { get; set; }
    public Guid? PriorityId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? TeamId { get; set; }
    public decimal? StoryPoints { get; set; }
    public string? ParentJiraIssueId { get; set; }
    public Guid? ParentIssueId { get; set; }
    public string? Summary { get; set; }
    public DateTime JiraCreatedAt { get; set; }
    public DateTime JiraUpdatedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }
}

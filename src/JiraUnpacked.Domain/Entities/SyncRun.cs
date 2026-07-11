using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class SyncRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public SyncRunKind Kind { get; set; }
    public int IssuesProcessed { get; set; }
    public string? Error { get; set; }
    public DateTime? NextRunAt { get; set; }
}

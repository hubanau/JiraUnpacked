using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class IssueChangeHistory : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string JiraHistoryId { get; set; } = default!;
    public string Field { get; set; } = default!;
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public string? Author { get; set; }
    public DateTime ChangedAt { get; set; }
}

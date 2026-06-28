using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A Jira Cloud connection owned by an End User (tenant). The first tenant-scoped
/// table — pulled forward minimally to enforce + prove isolation (FR-B1/B2).
/// Token encryption + Jira validation arrive in Phase 4.
/// </summary>
public class JiraConnection : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public string BaseUrl { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string ApiTokenEncrypted { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

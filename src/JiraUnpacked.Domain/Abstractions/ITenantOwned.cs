namespace JiraUnpacked.Domain.Abstractions;

/// <summary>A row scoped to a single tenant (End User), discriminated by EndUserId.</summary>
public interface ITenantOwned
{
    Guid EndUserId { get; set; }
}

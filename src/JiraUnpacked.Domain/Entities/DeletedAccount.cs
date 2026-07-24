namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// Proof that an erasure request was honoured. Deliberately NOT ITenantOwned — it must outlive
/// the tenant it records, so it carries no EndUserId, no query filter and no RLS policy, and
/// the FR-B2 gate is unaffected. Holds no personal data: only opaque ids and a timestamp.
/// </summary>
public sealed class DeletedAccount
{
    public Guid Id { get; set; }

    /// <summary>The erased account's id. The Users row is gone, so this is a dangling reference by design.</summary>
    public Guid DeletedUserId { get; set; }

    /// <summary>Who asked. Equals DeletedUserId for self-service deletion.</summary>
    public Guid RequestedBy { get; set; }

    public DateTime DeletedAt { get; set; }
}

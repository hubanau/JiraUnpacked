using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A named, per-project filter definition. Tenant-owned: it carries EndUserId, so it gets the
/// EF global query filter, the Postgres RLS policy, the central EndUserId -> Users erasure
/// cascade, and a case in the FR-B2 isolation gate.
///
/// Definition is stored as jsonb holding the same { conditions?, filter? } pair every filter
/// endpoint accepts. It is validated through the shared compiler before it is written, and
/// compiled through that same compiler when a client runs it — a saved filter is inert
/// storage, never a second path into the translator.
/// </summary>
public sealed class SavedFilter : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string Name { get; set; } = default!;
    public string DefinitionJson { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

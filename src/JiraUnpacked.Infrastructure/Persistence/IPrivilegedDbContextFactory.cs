namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Builds an AppDbContext on the 'Reader' connection — RLS-bypassing, read-only — with no
/// tenant set.
///
/// USE ONLY for cross-tenant scheduling metadata: DueProjectFinder, SyncRunRetentionJob's and
/// StuckRunReaperJob's (project, tenant) enumeration, and TokenEnvelopeUpgrader's scan. Never
/// read issue payloads with it, and never WRITE with it — every one of those callers writes
/// inside the owning tenant's scope, so writes stay under RLS.
///
/// That last rule is now enforced by the database as well as by convention: the role holds
/// SELECT and nothing else, so a write through this context fails rather than quietly
/// succeeding outside tenant isolation.
/// </summary>
public interface IPrivilegedDbContextFactory
{
    AppDbContext Create();
}

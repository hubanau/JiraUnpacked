namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Builds an AppDbContext on the privileged (RLS-bypassing) 'Default' connection with no tenant.
/// USE ONLY for cross-tenant scheduling metadata: DueProjectFinder, and SyncRunRetentionJob's
/// (project, tenant) enumeration. Never read issue payloads with it, and never WRITE with it —
/// retention deletes inside each tenant's scope so writes stay under RLS.
/// </summary>
public interface IPrivilegedDbContextFactory
{
    AppDbContext Create();
}

namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Builds an AppDbContext on the privileged (RLS-bypassing) 'Default' connection with no tenant.
/// USE ONLY for cross-tenant scheduling metadata (DueProjectFinder). Never read issue payloads with it.
/// </summary>
public interface IPrivilegedDbContextFactory
{
    AppDbContext Create();
}

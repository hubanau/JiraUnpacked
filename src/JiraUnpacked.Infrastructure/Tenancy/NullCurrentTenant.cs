using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>No tenant — used by design-time tooling, migrations, and the seeder.</summary>
public sealed class NullCurrentTenant : ICurrentTenant
{
    public Guid? EndUserId => null;
}

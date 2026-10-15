using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JiraUnpacked.Infrastructure.Persistence;

public sealed class PrivilegedDbContextFactory : IPrivilegedDbContextFactory
{
    private readonly string _connectionString;

    public PrivilegedDbContextFactory(IConfiguration configuration)
    {
        // 'Reader': read-only and BYPASSRLS. Deliberately NOT 'Default' — that is the owner,
        // and using it here put DDL rights in the running container for reads that never
        // needed them.
        // jit=off like the app's own sessions (see PostgresSessionOptions).
        _connectionString = PostgresSessionOptions.WithoutJit(configuration.GetConnectionString("Reader")
            ?? throw new InvalidOperationException(
                "Connection string 'Reader' (the read-only cross-tenant role) is not configured."));
    }

    public AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }
}

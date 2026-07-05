using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JiraUnpacked.Infrastructure.Persistence;

public sealed class PrivilegedDbContextFactory : IPrivilegedDbContextFactory
{
    private readonly string _connectionString;

    public PrivilegedDbContextFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
    }

    public AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }
}

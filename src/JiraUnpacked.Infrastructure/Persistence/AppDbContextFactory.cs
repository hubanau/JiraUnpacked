using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Used only by the EF Core tools (<c>dotnet ef</c>) at design time — never at
/// runtime. Lets migrations run against the Infrastructure project directly,
/// without spinning up the API host. Defaults to the local docker-compose
/// Postgres; override with the <c>ConnectionStrings__Default</c> env var
/// (e.g. to target RDS).
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=jiraunpacked;Username=jiraunpacked;Password=localdev";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? LocalDevConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}

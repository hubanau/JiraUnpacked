using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Spins up the API against a dedicated local/CI Postgres database
/// (`jiraunpacked_test`), applying migrations once on first use. Tests use
/// unique data (random emails/usernames) so they don't collide.
/// </summary>
public sealed class JiraUnpackedApiFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5432;Database=jiraunpacked_test;Username=jiraunpacked;Password=localdev";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Override the DbContext registration to point at the test database.
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));

            // Replace the "auth" fixed-window limiter with an unlimited policy so
            // integration tests are never throttled by repeated requests from 127.0.0.1.
            // Remove all previously registered IConfigureOptions<RateLimiterOptions> and
            // re-register with a no-op policy so AddPolicy("auth") doesn't throw a duplicate.
            var rateLimiterDescriptors = services
                .Where(d => d.ServiceType == typeof(IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>))
                .ToList();
            foreach (var d in rateLimiterDescriptors)
                services.Remove(d);

            services.AddRateLimiter(options =>
                options.AddPolicy("auth", _ =>
                    RateLimitPartition.GetNoLimiter(string.Empty)));

            // Replace the dev email sender with a capturing one so tests can read the code.
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(JiraUnpacked.Domain.Abstractions.IEmailSender));
            if (emailDescriptor is not null)
            {
                services.Remove(emailDescriptor);
            }
            services.AddSingleton<CapturingEmailSender>();
            services.AddSingleton<JiraUnpacked.Domain.Abstractions.IEmailSender>(
                sp => sp.GetRequiredService<CapturingEmailSender>());
        });
    }

    public async Task EnsureDatabaseAsync()
    {
        // Migrate via a standalone context so the database exists and is migrated
        // BEFORE the host is built. The app's startup admin-seeder runs during host
        // construction; accessing Services here (to migrate) would build the host and
        // run that seeder against a not-yet-created database (fails on a fresh CI DB).
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
    }
}

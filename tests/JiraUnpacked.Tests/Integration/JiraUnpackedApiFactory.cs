using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
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
    /// <summary>
    /// The test database's name. Overridable through JU_TEST_DATABASE so two branches can run
    /// their integration suites at the same time without sharing rows or a migration history —
    /// the suite never truncates, so a shared database is exactly where parallel runs collide.
    /// MigrateAsync creates the database on first use; the roles are cluster-wide.
    /// </summary>
    public static readonly string TestDatabase =
        Environment.GetEnvironmentVariable("JU_TEST_DATABASE") is { Length: > 0 } name ? name : "jiraunpacked_test";

    public static readonly string TestConnectionString =
        $"Host=localhost;Port=5432;Database={TestDatabase};Username=jiraunpacked;Password=localdev";

    // Runtime role: RLS-enforced (set up by the AddTenantRls migration).
    public static readonly string TestAppConnectionString =
        $"Host=localhost;Port=5432;Database={TestDatabase};Username=jiraunpacked_app;Password=localapp";

    // Background-reader role: read-only and BYPASSRLS (set up by the AddReaderRole migration).
    public static readonly string TestReaderConnectionString =
        $"Host=localhost;Port=5432;Database={TestDatabase};Username=jiraunpacked_reader;Password=localreader";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Disable the Quartz tick scheduler in tests: a stray tick could sync real
        // WireMock-configured projects concurrently with test assertions, causing flakes.
        // UseSetting (not ConfigureAppConfiguration) is required here: Program.cs reads
        // builder.Configuration synchronously in its top-level statements (to decide whether
        // to register Quartz) before WebApplicationFactory's ConfigureAppConfiguration hooks
        // are woven into the host build pipeline. UseSetting seeds WebApplicationBuilder's
        // configuration at construction time, the same mechanism UseEnvironment relies on above.
        builder.UseSetting("Sync:SchedulerEnabled", "false");

        // ConnectionStrings:Default backs IPrivilegedDbContextFactory (constructed straight from
        // IConfiguration, not from the AppDbContext registration overridden below). Without this,
        // any DI-resolved privileged context — e.g. SyncRunRetentionJob's cross-tenant read —
        // would connect to the unmigrated "jiraunpacked" dev database instead of the test one.
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString);
        builder.UseSetting("ConnectionStrings:Reader", TestReaderConnectionString);

        // Integration tests stand in for Jira with WireMock/fake servers on http://localhost, which
        // the production base-URL policy (https + *.atlassian.net only) rightly refuses. Relax it
        // here, and only here; JiraBaseUrlValidatorTests pins the default policy.
        builder.UseSetting("Jira:AllowInsecureHttp", "true");
        builder.UseSetting("Jira:AllowedHostSuffixes:0", ".atlassian.net");
        builder.UseSetting("Jira:AllowedHostSuffixes:1", "localhost");
        builder.UseSetting("Jira:AllowedHostSuffixes:2", "127.0.0.1");

        // Override the DbContext registration to point at the test database.
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddScoped<TenantConnectionInterceptor>();
            services.AddDbContext<AppDbContext>((sp, options) =>
                options.UseNpgsql(TestAppConnectionString)
                       .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

            // Replace the "auth" and "sync" limiters with unlimited policies so integration
            // tests are never throttled by repeated requests from 127.0.0.1 or a shared test
            // user. Remove all previously registered IConfigureOptions<RateLimiterOptions> and
            // re-register both with no-op policies so AddPolicy(...) doesn't throw a duplicate.
            var rateLimiterDescriptors = services
                .Where(d => d.ServiceType == typeof(IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>))
                .ToList();
            foreach (var d in rateLimiterDescriptors)
                services.Remove(d);

            services.AddRateLimiter(options =>
            {
                options.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("sync", _ => RateLimitPartition.GetNoLimiter(string.Empty));
            });

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
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        await db.Database.MigrateAsync();
    }
}

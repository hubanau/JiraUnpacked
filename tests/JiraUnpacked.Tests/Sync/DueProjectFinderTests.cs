// tests/JiraUnpacked.Tests/Sync/DueProjectFinderTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Infrastructure.Time;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JiraUnpacked.Tests.Sync;

public sealed class DueProjectFinderTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public DueProjectFinderTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // A privileged context (Default superuser conn) for seeding across tenants, mirroring the finder.
    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    private static async Task<Guid> SeedProjectWithQueryAsync(AppDbContext db, Guid tenant, int intervalMinutes)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        db.JiraConnections.Add(new JiraConnection { Id = connId, EndUserId = tenant, BaseUrl = "https://x", Email = "e", ApiTokenEncrypted = "c", CreatedAt = DateTime.UtcNow });
        db.JiraProjects.Add(new JiraProject { Id = projId, EndUserId = tenant, JiraConnectionId = connId, JiraProjectKey = "ACME", JiraProjectId = "1", SyncIntervalMinutes = intervalMinutes, CreatedAt = DateTime.UtcNow });
        db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, Jql = "project = ACME", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return projId;
    }

    private static DueProjectFinder Finder() =>
        new(new PrivilegedDbContextFactory(_config()), new SystemClock());

    private static IConfiguration _config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Default"] = JiraUnpackedApiFactory.TestConnectionString }).Build();

    [Fact]
    public async Task Project_with_no_successful_run_is_due()
    {
        var tenant = Guid.NewGuid();
        Guid projId;
        await using (var db = Privileged()) projId = await SeedProjectWithQueryAsync(db, tenant, 60);

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    [Fact]
    public async Task Recently_synced_project_is_not_due()
    {
        var tenant = Guid.NewGuid();
        await using var db = Privileged();
        var projId = await SeedProjectWithQueryAsync(db, tenant, 60);
        db.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, StartedAt = DateTime.UtcNow.AddMinutes(-5), FinishedAt = DateTime.UtcNow.AddMinutes(-5), Status = SyncRunStatus.Succeeded, IssuesProcessed = 1 });
        await db.SaveChangesAsync();

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }

    [Fact]
    public async Task Project_with_a_running_run_is_excluded()
    {
        var tenant = Guid.NewGuid();
        await using var db = Privileged();
        var projId = await SeedProjectWithQueryAsync(db, tenant, 60);
        db.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, StartedAt = DateTime.UtcNow, Status = SyncRunStatus.Running, IssuesProcessed = 0 });
        await db.SaveChangesAsync();

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }
}

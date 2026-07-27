// tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Retention;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncRunRetentionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SyncRunRetentionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedRunAsync(
        Guid tenant, Guid projectId, DateTime startedAt, SyncRunStatus status, SyncRunKind kind)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,@st,@k,0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId), ("s", startedAt),
            ("st", status.ToString()), ("k", kind.ToString()));
    }

    [Fact]
    public async Task Purge_removes_old_runs_but_keeps_the_watermark_anchor_and_running_runs()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await Tenancy.TenantDeletionTestData.SeedFullGraphAsync(tenant, seedSyncRun: false);
        var now = DateTime.UtcNow;

        // Three old succeeded syncs (well past the cutoff) and one old Running run.
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-400), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-300), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-200), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-350), SyncRunStatus.Running, SyncRunKind.Sync);

        using var scope = _factory.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<SyncRunRetentionJob>(scope.ServiceProvider);
        await job.RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var remaining = await db.SyncRuns.IgnoreQueryFilters()
            .Where(r => r.JiraProjectId == seed.ProjectId)
            .ToListAsync();

        // The newest Succeeded+Sync run is the incremental watermark's anchor: deleting it
        // would make the next sync re-pull everything from the beginning.
        Assert.Contains(remaining, r => r.Status == SyncRunStatus.Succeeded
                                        && r.Kind == SyncRunKind.Sync
                                        && r.StartedAt.Date == now.AddDays(-200).Date);

        // A Running run is never purged at any age — removing it would break the
        // AlreadyRunning guard and orphan an in-flight sync.
        Assert.Contains(remaining, r => r.Status == SyncRunStatus.Running);

        // The two older succeeded runs are gone.
        Assert.DoesNotContain(remaining, r => r.StartedAt.Date == now.AddDays(-400).Date);
        Assert.DoesNotContain(remaining, r => r.StartedAt.Date == now.AddDays(-300).Date);
    }

    [Fact]
    public async Task Purge_leaves_recent_runs_alone()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await Tenancy.TenantDeletionTestData.SeedFullGraphAsync(tenant, seedSyncRun: false);
        var now = DateTime.UtcNow;

        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-5), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-3), SyncRunStatus.Failed, SyncRunKind.Sync);

        using var scope = _factory.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<SyncRunRetentionJob>(scope.ServiceProvider);
        await job.RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var count = await db.SyncRuns.IgnoreQueryFilters()
            .CountAsync(r => r.JiraProjectId == seed.ProjectId);
        Assert.Equal(2, count);
    }
}

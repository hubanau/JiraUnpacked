// tests/JiraUnpacked.Tests/Integration/SyncConcurrencyTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncConcurrencyTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SyncConcurrencyTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Seeds a tenant + connection + project directly; returns (tenant, projectId).
    private async Task<(Guid tenant, Guid projectId)> SeedProjectAsync()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        return (tenant, projId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Database_rejects_a_second_running_run_for_a_project()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        async Task InsertRunning(Guid id) => await Exec(conn,
            @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,now(),'Running','Sync',0)",
            ("i", id), ("t", tenant), ("p", projectId));

        await InsertRunning(Guid.NewGuid());   // first Running row is fine

        // The partial unique index must reject the second, with SqlState 23505.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertRunning(Guid.NewGuid()));
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task A_sync_is_rejected_while_a_run_is_already_in_progress()
    {
        // A racy "two simultaneous first-inserts" test is non-deterministic: Task.WhenAll does not
        // guarantee overlap, so if the calls serialize the first finishes (-> Failed) and the second
        // legitimately starts a NEW run, giving runCount == 2. The DB-level race guarantee is proven
        // deterministically by Database_rejects_a_second_running_run_for_a_project above; here we
        // prove the orchestrator's integration guard deterministically by seeding an in-progress run.
        var (tenant, projectId) = await SeedProjectAsync();
        await using (var seed = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await seed.OpenAsync();
            await Exec(seed, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,now(),'Running','Sync',0)",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId));
        }

        async Task<SyncOutcome> RunOnce()
        {
            var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
            using (tenantScope.BeginScope(tenant))
            using (var scope = _factory.Services.CreateScope())
            {
                var orch = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                return (await orch.RunProjectAsync(projectId)).Outcome;
            }
        }

        var outcomes = await Task.WhenAll(RunOnce(), RunOnce());

        // With a run already in progress, every call is rejected and no new run is created.
        Assert.All(outcomes, o => Assert.Equal(SyncOutcome.AlreadyRunning, o));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var runCount = await db.SyncRuns.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(1, runCount);   // still only the seeded in-progress run
    }
}

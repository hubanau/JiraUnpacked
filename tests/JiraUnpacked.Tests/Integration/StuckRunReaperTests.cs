// tests/JiraUnpacked.Tests/Integration/StuckRunReaperTests.cs
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class StuckRunReaperTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public StuckRunReaperTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options, new NullCurrentTenant());

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

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

    private static async Task SeedRunAsync(Guid tenant, Guid projectId, DateTime startedAt) // Running
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,'Running','Sync',0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId), ("s", startedAt));
    }

    private StuckRunReaperJob NewJob(IServiceScope scope) =>
        ActivatorUtilities.CreateInstance<StuckRunReaperJob>(scope.ServiceProvider);

    [Fact]
    public async Task Old_running_run_is_marked_failed()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunAsync(tenant, projectId, DateTime.UtcNow.AddMinutes(-300)); // well past 120m default

        using (var scope = _factory.Services.CreateScope())
            await NewJob(scope).RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var run = await db.SyncRuns.IgnoreQueryFilters().SingleAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
        // And the project is unblocked: no Running row remains, so the partial index would admit a new one.
        Assert.False(await db.SyncRuns.IgnoreQueryFilters().AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running));
    }

    [Fact]
    public async Task Recent_running_run_is_left_alone()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunAsync(tenant, projectId, DateTime.UtcNow.AddMinutes(-5)); // an in-flight sync

        using (var scope = _factory.Services.CreateScope())
            await NewJob(scope).RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var run = await db.SyncRuns.IgnoreQueryFilters().SingleAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(SyncRunStatus.Running, run.Status);   // never kill an in-flight run
    }
}

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

    /// <summary>A Running run that last reported progress at the given moment.</summary>
    private static async Task SeedRunWithProgressAsync(
        Guid tenant, Guid projectId, DateTime startedAt, DateTime lastProgressAt)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""LastProgressAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,@l,'Running','Sync',0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId), ("s", startedAt), ("l", lastProgressAt));
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

    /// <summary>The project's single run status, read privileged — the reaper writes per tenant.</summary>
    private async Task<string> StatusOf(Guid projectId)
    {
        await using var db = Privileged();
        var run = await db.SyncRuns.IgnoreQueryFilters().SingleAsync(r => r.JiraProjectId == projectId);
        return run.Status.ToString();
    }

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

    /// <summary>
    /// The defect this fixes. A 92,673-issue sync ran for over three hours and was marked failed
    /// at the two-hour mark while it was still working — the reaper compared StartedAt against a
    /// fixed timeout, so "long" and "stuck" were the same thing to it.
    ///
    /// Age alone cannot decide. A run that is still publishing progress is alive whatever its age.
    /// </summary>
    [Fact]
    public async Task A_long_run_that_is_still_reporting_is_left_alone()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunWithProgressAsync(
            tenant, projectId,
            startedAt: DateTime.UtcNow.AddMinutes(-300),      // far past the 120m timeout
            lastProgressAt: DateTime.UtcNow.AddMinutes(-2));  // but it reported two minutes ago

        using var scope = _factory.Services.CreateScope();
        await NewJob(scope).RunOnceAsync(CancellationToken.None);

        Assert.Equal("Running", await StatusOf(projectId));
    }

    /// <summary>
    /// The case the reaper exists for, which the fix must not weaken: a run whose process died.
    /// It stops reporting, so silence — not age — is what condemns it.
    /// </summary>
    [Fact]
    public async Task A_run_that_stopped_reporting_is_still_reaped()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunWithProgressAsync(
            tenant, projectId,
            startedAt: DateTime.UtcNow.AddMinutes(-300),
            lastProgressAt: DateTime.UtcNow.AddMinutes(-300)); // last sign of life, five hours ago

        using var scope = _factory.Services.CreateScope();
        await NewJob(scope).RunOnceAsync(CancellationToken.None);

        Assert.Equal("Failed", await StatusOf(projectId));
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

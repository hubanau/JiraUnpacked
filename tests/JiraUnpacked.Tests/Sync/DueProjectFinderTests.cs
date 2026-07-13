// tests/JiraUnpacked.Tests/Sync/DueProjectFinderTests.cs
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Time;
using JiraUnpacked.Tests.Integration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace JiraUnpacked.Tests.Sync;

public sealed class DueProjectFinderTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public DueProjectFinderTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Seed JiraConnection + JiraProject + JiraQuery for a fresh tenant via the privileged
    // superuser connection (bypasses RLS, never touches EF / StampTenant).
    private static async Task<Guid> SeedProjectWithQueryAsync(Guid tenant, int intervalMinutes)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        await using (var c1 = conn.CreateCommand())
        {
            c1.CommandText = @"INSERT INTO ""JiraConnections""
                (""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
                VALUES (@id,@t,'https://x','e','c',now())";
            c1.Parameters.AddWithValue("id", connId);
            c1.Parameters.AddWithValue("t", tenant);
            await c1.ExecuteNonQueryAsync();
        }
        await using (var c2 = conn.CreateCommand())
        {
            c2.CommandText = @"INSERT INTO ""JiraProjects""
                (""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"")
                VALUES (@id,@t,@conn,'ACME','10001',@interval,now())";
            c2.Parameters.AddWithValue("id", projId);
            c2.Parameters.AddWithValue("t", tenant);
            c2.Parameters.AddWithValue("conn", connId);
            c2.Parameters.AddWithValue("interval", intervalMinutes);
            await c2.ExecuteNonQueryAsync();
        }
        await using (var c3 = conn.CreateCommand())
        {
            c3.CommandText = @"INSERT INTO ""JiraQueries""
                (""Id"",""EndUserId"",""JiraProjectId"",""Jql"",""CreatedAt"")
                VALUES (@id,@t,@proj,'project = ACME',now())";
            c3.Parameters.AddWithValue("id", Guid.NewGuid());
            c3.Parameters.AddWithValue("t", tenant);
            c3.Parameters.AddWithValue("proj", projId);
            await c3.ExecuteNonQueryAsync();
        }
        return projId;
    }

    private static async Task SeedSyncRunAsync(Guid tenant, Guid projId, string status,
        DateTime startedAt, DateTime? finishedAt = null, int issuesProcessed = 0, string kind = "Sync")
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""SyncRuns""
            (""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""FinishedAt"",""Status"",""IssuesProcessed"",""Kind"")
            VALUES (@id,@t,@proj,@started,@finished,@status,@issues,@kind)";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("proj", projId);
        cmd.Parameters.AddWithValue("started", startedAt);
        cmd.Parameters.AddWithValue("finished", finishedAt.HasValue ? (object)finishedAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("issues", issuesProcessed);
        cmd.Parameters.AddWithValue("kind", kind);
        await cmd.ExecuteNonQueryAsync();
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
        var projId = await SeedProjectWithQueryAsync(tenant, 60);

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    [Fact]
    public async Task Recently_synced_project_is_not_due()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectWithQueryAsync(tenant, 60);
        var syncedAt = DateTime.UtcNow.AddMinutes(-5);
        await SeedSyncRunAsync(tenant, projId, "Succeeded", syncedAt, syncedAt, issuesProcessed: 1);

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }

    [Fact]
    public async Task Project_with_a_running_run_is_excluded()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectWithQueryAsync(tenant, 60);
        await SeedSyncRunAsync(tenant, projId, "Running", DateTime.UtcNow);

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }

    // Proves the Important fix (final review): a Rederive run must NOT reset the
    // "last success" clock used to decide whether a project is due for its next sync.
    [Fact]
    public async Task Rederive_run_does_not_mark_a_due_project_not_due()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectWithQueryAsync(tenant, 60); // 60-minute interval

        // Last real sync was 90 minutes ago -> overdue against the 60-minute interval.
        await SeedSyncRunAsync(tenant, projId, "Succeeded", DateTime.UtcNow.AddMinutes(-90),
            DateTime.UtcNow.AddMinutes(-89), kind: "Sync");
        // A recent rederive must not count as the "last success" for scheduling purposes.
        await SeedSyncRunAsync(tenant, projId, "Succeeded", DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow, kind: "Rederive");

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }
}

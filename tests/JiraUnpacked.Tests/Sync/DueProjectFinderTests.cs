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

    // Seed JiraConnection + JiraProject (+ optionally a JiraQuery) for a fresh tenant via the
    // privileged superuser connection (bypasses RLS, never touches EF / StampTenant).
    private static async Task<Guid> SeedProjectAsync(Guid tenant, int intervalMinutes, bool withQuery = true)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        // The EndUserId -> Users cascade FK (Phase 7 Spec 1) requires a backing Users row.
        await SeedTenantUserAsync(conn, tenant);

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
        if (withQuery)
        {
            await using var c3 = conn.CreateCommand();
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

    /// <summary>
    /// The scheduled half of the whole-project-default fix. The finder used to require at least
    /// one JiraQuery, so a project added through the UI — which has no way to create one — was
    /// skipped by every tick, forever, without a trace. The orchestrator now syncs such a project
    /// in full, and this is what gets it there on a schedule rather than only by hand.
    /// </summary>
    [Fact]
    public async Task Project_with_no_query_is_due()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60, withQuery: false);

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    // Setup-only: seeds the Users row a synthetic tenant id needs to satisfy the new
    // EndUserId -> Users cascade FK (Phase 7 Spec 1). Never authenticated against; does not
    // change what any test in this file proves.
    private static async Task SeedTenantUserAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""Users""
            (""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"")
            VALUES (@id,@email,@username,'test-fixture-not-authenticated','EndUser','Active',now())";
        cmd.Parameters.AddWithValue("id", tenant);
        cmd.Parameters.AddWithValue("email", $"tenant-{tenant:N}@example.com");
        cmd.Parameters.AddWithValue("username", $"tenant-{tenant:N}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedSyncRunAsync(Guid tenant, Guid projId, string status,
        DateTime startedAt, DateTime? finishedAt = null, int issuesProcessed = 0, string kind = "Sync",
        DateTime? nextRunAt = null)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""SyncRuns""
            (""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""FinishedAt"",""Status"",""IssuesProcessed"",""Kind"",""NextRunAt"")
            VALUES (@id,@t,@proj,@started,@finished,@status,@issues,@kind,@next)";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("proj", projId);
        cmd.Parameters.AddWithValue("started", startedAt);
        cmd.Parameters.AddWithValue("finished", finishedAt.HasValue ? (object)finishedAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("issues", issuesProcessed);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("next", nextRunAt.HasValue ? (object)nextRunAt.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static DueProjectFinder Finder() =>
        new(new PrivilegedDbContextFactory(_config()), new SystemClock());

    private static IConfiguration _config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The finder reads through the reader role, so the test must too — using the
                // owner here would pass while proving nothing about the role the app uses.
                ["ConnectionStrings:Default"] = JiraUnpackedApiFactory.TestConnectionString,
                ["ConnectionStrings:Reader"] = JiraUnpackedApiFactory.TestReaderConnectionString,
            }).Build();

    [Fact]
    public async Task Project_with_no_successful_run_is_due()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    [Fact]
    public async Task Recently_synced_project_is_not_due()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
        var syncedAt = DateTime.UtcNow.AddMinutes(-5);
        await SeedSyncRunAsync(tenant, projId, "Succeeded", syncedAt, syncedAt, issuesProcessed: 1);

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }

    [Fact]
    public async Task Project_with_a_running_run_is_excluded()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
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
        var projId = await SeedProjectAsync(tenant, 60); // 60-minute interval

        // Last real sync was 90 minutes ago -> overdue against the 60-minute interval.
        await SeedSyncRunAsync(tenant, projId, "Succeeded", DateTime.UtcNow.AddMinutes(-90),
            DateTime.UtcNow.AddMinutes(-89), kind: "Sync");
        // A recent rederive must not count as the "last success" for scheduling purposes.
        await SeedSyncRunAsync(tenant, projId, "Succeeded", DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow, kind: "Rederive");

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    private static async Task SetUserStatusAsync(Guid userId, string status)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE ""Users"" SET ""Status"" = @s WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>FR-A5: a blocked End User's scheduled syncs are paused, and resume on unblock.</summary>
    [Fact]
    public async Task A_blocked_users_projects_are_not_due_until_unblocked()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
        await SetUserStatusAsync(tenant, "Blocked");

        Assert.DoesNotContain(await Finder().GetDueProjectsAsync(), d => d.ProjectId == projId);

        await SetUserStatusAsync(tenant, "Active");
        Assert.Contains(await Finder().GetDueProjectsAsync(), d => d.ProjectId == projId);
    }

    /// <summary>
    /// A failed run records when to try again. The finder used to look only at the last success,
    /// so a failing project was retried every minute for as long as the fault lasted.
    /// </summary>
    [Fact]
    public async Task A_failed_project_waits_for_its_backoff()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
        var failedAt = DateTime.UtcNow.AddMinutes(-2);
        await SeedSyncRunAsync(tenant, projId, "Failed", failedAt, failedAt, nextRunAt: DateTime.UtcNow.AddMinutes(3));

        Assert.DoesNotContain(await Finder().GetDueProjectsAsync(), d => d.ProjectId == projId);
    }

    [Fact]
    public async Task A_failed_project_is_due_once_its_backoff_has_passed()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
        var failedAt = DateTime.UtcNow.AddMinutes(-10);
        await SeedSyncRunAsync(tenant, projId, "Failed", failedAt, failedAt, nextRunAt: DateTime.UtcNow.AddMinutes(-5));

        Assert.Contains(await Finder().GetDueProjectsAsync(), d => d.ProjectId == projId);
    }

    /// <summary>Pressing Stop must not be undone by the next tick a minute later.</summary>
    [Fact]
    public async Task A_stopped_project_is_not_restarted_before_its_next_run()
    {
        var tenant = Guid.NewGuid();
        var projId = await SeedProjectAsync(tenant, 60);
        // Last success long ago, so the old rule would have called it due at once.
        await SeedSyncRunAsync(tenant, projId, "Succeeded", DateTime.UtcNow.AddHours(-5), DateTime.UtcNow.AddHours(-5));
        var stoppedAt = DateTime.UtcNow.AddMinutes(-1);
        await SeedSyncRunAsync(tenant, projId, "Cancelled", stoppedAt, stoppedAt, nextRunAt: stoppedAt.AddMinutes(60));

        Assert.DoesNotContain(await Finder().GetDueProjectsAsync(), d => d.ProjectId == projId);
    }
}

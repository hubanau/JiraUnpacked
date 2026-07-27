using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Time;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Sync;

public sealed class ChangelogIngestTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ChangelogIngestTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    // Runs the ingest service under a fresh DI scope with the ambient tenant set (mirrors the tick job).
    private async Task IngestAsync(Guid tenant, Guid projectId, JiraCredentials creds)
    {
        var scopeSetter = _factory.Services.GetRequiredService<ITenantScope>();
        using (scopeSetter.BeginScope(tenant))
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IChangelogIngestService>();
            await svc.IngestProjectAsync(projectId, creds, CancellationToken.None);
        }
    }

    private async Task<long> RawChangelogCount(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new JiraUnpacked.Infrastructure.Tenancy.NullCurrentTenant());
        return await db.RawChangelogs.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
    }

    // Seeds connection+project+one RawIssueSnapshot (raw SQL) with the given embedded changelog JSON; returns (tenant, projectId, connId).
    private async Task<(Guid tenant, Guid projectId)> SeedIssueAsync(string embeddedChangelogJson)
    {
        var tenant = Guid.NewGuid(); var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        var payload = $"{{\"id\":\"10001\",\"key\":\"ACME-1\",\"changelog\":{embeddedChangelogJson}}}";
        await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        // The EndUserId -> Users cascade FK (Phase 7 Spec 1) requires a backing Users row.
        await Exec(conn, @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"") VALUES (@id,@email,@username,'test-fixture-not-authenticated','EndUser','Active',now())",
            ("id", tenant), ("email", $"tenant-{tenant:N}@example.com"), ("username", $"tenant-{tenant:N}"));
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@id,@t,'https://x','e','c',now())", ("id", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@id,@t,@c,'ACME','1',60,now())", ("id", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@id,@t,@p,'10001',@payload::jsonb,now())", ("id", Guid.NewGuid()), ("t", tenant), ("p", projId), ("payload", payload));
        return (tenant, projId);
    }
    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string, object)[] ps)
    { await using var c = conn.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v); await c.ExecuteNonQueryAsync(); }

    [Fact]
    public async Task Embedded_complete_changelog_is_ingested_without_a_dedicated_call()
    {
        // total == histories.length -> complete; no dedicated endpoint stubbed, so a call would 404/throw.
        var embedded = "{\"startAt\":0,\"maxResults\":100,\"total\":1,\"histories\":[{\"id\":\"h1\",\"items\":[{\"field\":\"status\"}]}]}";
        var (tenant, projId) = await SeedIssueAsync(embedded);

        await IngestAsync(tenant, projId, new JiraCredentials(_jira.Url!, "e@x.com", "t"));

        Assert.Equal(1, await RawChangelogCount(projId));
        Assert.Empty(_jira.LogEntries); // no dedicated changelog call
    }

    [Fact]
    public async Task Truncated_changelog_is_paged_from_the_dedicated_endpoint()
    {
        // embedded says total=2 but only 1 history -> truncated -> page dedicated endpoint.
        var embedded = "{\"startAt\":0,\"maxResults\":1,\"total\":2,\"histories\":[{\"id\":\"h1\"}]}";
        var (tenant, projId) = await SeedIssueAsync(embedded);
        _jira.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { startAt = 0, maxResults = 100, total = 2, isLast = true,
               values = new object[] { new { id = "h1" }, new { id = "h2" } } }));

        await IngestAsync(tenant, projId, new JiraCredentials(_jira.Url!, "e@x.com", "t"));

        Assert.Equal(1, await RawChangelogCount(projId));
        Assert.NotEmpty(_jira.LogEntries); // dedicated endpoint used
    }

    [Fact]
    public async Task Reingest_is_idempotent()
    {
        var embedded = "{\"startAt\":0,\"maxResults\":100,\"total\":1,\"histories\":[{\"id\":\"h1\",\"items\":[{\"field\":\"status\"}]}]}";
        var (tenant, projId) = await SeedIssueAsync(embedded);
        var creds = new JiraCredentials(_jira.Url!, "e@x.com", "t");

        await IngestAsync(tenant, projId, creds);
        await IngestAsync(tenant, projId, creds);

        Assert.Equal(1, await RawChangelogCount(projId)); // upsert, not duplicate
    }

    [Fact]
    public async Task Reingest_of_a_multi_issue_project_upserts_each_row_once()
    {
        // Exercises the hoisted (single, post-loop) SaveChangesAsync across several issues in one project:
        // the existingByJiraId dictionary must resolve every snapshot to its own row on the second pass,
        // not just the first one it saw, so re-syncing never produces duplicate RawChangelog rows.
        var embedded = "{\"startAt\":0,\"maxResults\":100,\"total\":1,\"histories\":[{\"id\":\"h1\",\"items\":[{\"field\":\"status\"}]}]}";
        var tenant = Guid.NewGuid(); var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"") VALUES (@id,@email,@username,'test-fixture-not-authenticated','EndUser','Active',now())",
            ("id", tenant), ("email", $"tenant-{tenant:N}@example.com"), ("username", $"tenant-{tenant:N}"));
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@id,@t,'https://x','e','c',now())", ("id", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@id,@t,@c,'ACME','1',60,now())", ("id", projId), ("t", tenant), ("c", connId));
        foreach (var jiraIssueId in new[] { "20001", "20002", "20003" })
        {
            var payload = $"{{\"id\":\"{jiraIssueId}\",\"key\":\"ACME-{jiraIssueId}\",\"changelog\":{embedded}}}";
            await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@id,@t,@p,@jid,@payload::jsonb,now())",
                ("id", Guid.NewGuid()), ("t", tenant), ("p", projId), ("jid", jiraIssueId), ("payload", payload));
        }
        var creds = new JiraCredentials(_jira.Url!, "e@x.com", "t");

        await IngestAsync(tenant, projId, creds);
        await IngestAsync(tenant, projId, creds);

        Assert.Equal(3, await RawChangelogCount(projId)); // one row per issue, no duplicates from the hoisted save
    }
}

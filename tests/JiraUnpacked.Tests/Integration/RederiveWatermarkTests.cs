// tests/JiraUnpacked.Tests/Integration/RederiveWatermarkTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

// Proves the Critical fix (final review): a Rederive run must NOT advance the
// incremental-sync watermark used by SyncOrchestrator.RunProjectAsync. Only the
// most recent Kind=Sync, Status=Succeeded run's StartedAt should seed the
// "updated >= ..." clause.
public sealed class RederiveWatermarkTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public RederiveWatermarkTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    private void StubValidAndProject()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredProjectAsync()
    {
        StubValidAndProject();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        return (client, project.Id);
    }

    // Privileged read (bypasses RLS) to resolve the tenant that owns the freshly-configured
    // project, so we can seed SyncRuns rows for it directly via SQL.
    private async Task<Guid> ResolveTenantAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var project = await db.JiraProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId);
        return project.EndUserId;
    }

    private static async Task SeedSyncRunAsync(Guid tenant, Guid projId, string kind, string status,
        DateTime startedAt, DateTime? finishedAt)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""SyncRuns""
            (""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""FinishedAt"",""Status"",""IssuesProcessed"",""Kind"")
            VALUES (@id,@t,@proj,@started,@finished,@status,0,@kind)";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("proj", projId);
        cmd.Parameters.AddWithValue("started", startedAt);
        cmd.Parameters.AddWithValue("finished", finishedAt.HasValue ? (object)finishedAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("kind", kind);
        await cmd.ExecuteNonQueryAsync();
    }

    // The issue the 2024-01-01 sync stored. Without it the fixture would describe a project whose
    // "successful" sync left nothing behind — the very state the orchestrator now refuses to treat
    // as a watermark — and the run under test would correctly do a full pull instead, proving
    // nothing about Rederive. Seeding it keeps the premise coherent; the Sync-vs-Rederive property
    // this test exists for is untouched.
    private static async Task SeedSnapshotAsync(Guid tenant, Guid projId, DateTime fetchedAt)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""RawIssueSnapshots""
            (""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"")
            VALUES (@id,@t,@proj,@issue,'{}'::jsonb,@fetched)";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("proj", projId);
        cmd.Parameters.AddWithValue("issue", "10001");
        cmd.Parameters.AddWithValue("fetched", fetchedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rederive_succeeded_run_does_not_advance_the_sync_watermark()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        var tenant = await ResolveTenantAsync(projectId);

        // Last real sync: 2024-01-01. Later rederive: 2024-06-01 — must NOT become the watermark.
        await SeedSyncRunAsync(tenant, projectId, "Sync", "Succeeded",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        await SeedSnapshotAsync(tenant, projectId, new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        await SeedSyncRunAsync(tenant, projectId, "Rederive", "Succeeded",
            new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 6, 1, 0, 1, 0, DateTimeKind.Utc));

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

        var resp = await SyncWait.SyncAsync(client, projectId);
        resp.EnsureSuccessStatusCode();

        var searchCalls = _jira.LogEntries.Where(e => e.RequestMessage.Path == "/rest/api/3/search/jql").ToList();
        Assert.Contains(searchCalls, e =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!);
            var jql = doc.RootElement.GetProperty("jql").GetString()!;
            return jql.Contains("updated >= \"2024-01-01 00:00") && !jql.Contains("2024-06-01");
        });
    }
}

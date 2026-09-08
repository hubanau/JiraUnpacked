using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class DerivationHistoryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public DerivationHistoryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    // Two issues: epic ACME-1 (10001) and story ACME-2 (10002, parent 10001); the story has one changelog entry.
    private void StubSearchEpicAndStory()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             {
                 issues = new object[] {
                     new { id = "10001", key = "ACME-1", fields = new { summary = "Epic", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-01T00:00:00.000+0000" },
                           changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } },
                     new { id = "10002", key = "ACME-2", fields = new { summary = "Story", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", parent = new { id = "10001", key = "ACME-1" } },
                           changelog = new { startAt = 0, maxResults = 100, total = 1, histories = new object[] {
                               new { id = "h1", author = new { displayName = "Alice" }, created = "2024-01-02T09:00:00.000+0000",
                                     items = new object[] { new { field = "status", fromString = "To Do", toString = "In Progress" } } } } } }
                 }
             }));
    }

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    // NOTE: the end-to-end "/sync derives change-history + hierarchy" test lives in TR6,
    // where ingest + derivation are wired into the sync orchestrator. At TR5, derivation is
    // exercised through /rederive over seeded raw (below).

    [Fact]
    public async Task Rederive_builds_history_and_parent_from_seeded_raw()
    {
        // Configure a project (no sync); seed two RawIssueSnapshots + the story's RawChangelog via raw SQL, then rederive.
        StubSearchEpicAndStory(); // only for connection validation/config calls
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        // resolve the tenant (EndUserId) of this project via privileged read
        Guid tenant;
        await using (var db = Privileged())
            tenant = await db.JiraProjects.IgnoreQueryFilters().Where(p => p.Id == project.Id).Select(p => p.EndUserId).FirstAsync();

        await using (var conn2 = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn2.OpenAsync();
            string epic = "{\"id\":\"10001\",\"key\":\"ACME-1\",\"fields\":{\"summary\":\"Epic\",\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-01T00:00:00.000+0000\"}}";
            string story = "{\"id\":\"10002\",\"key\":\"ACME-2\",\"fields\":{\"summary\":\"Story\",\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-02T00:00:00.000+0000\",\"parent\":{\"id\":\"10001\"}}}";
            string cl = "{\"histories\":[{\"id\":\"h1\",\"author\":{\"displayName\":\"Alice\"},\"created\":\"2024-01-02T09:00:00.000+0000\",\"items\":[{\"field\":\"status\",\"fromString\":\"To Do\",\"toString\":\"In Progress\"}]}]}";
            await Exec(conn2, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", epic));
            await Exec(conn2, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10002',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", story));
            await Exec(conn2, @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10002',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", cl));
        }

        await SyncWait.RederiveAsync(client, project.Id);
        await SyncWait.RederiveAsync(client, project.Id); // append-only history must stay idempotent

        await using var vdb = Privileged();
        var history = await vdb.IssueChangeHistory.IgnoreQueryFilters().Where(h => h.JiraProjectId == project.Id).ToListAsync();
        Assert.Contains(history, h => h.JiraIssueId == "10002" && h.Field == "status" && h.ToValue == "In Progress" && h.JiraHistoryId == "h1");
        Assert.Single(history); // re-derive did not duplicate the change-history row
        // Scope reads to THIS test's project: other tests seed issues with the same Jira ids ("10001"/"10002"),
        // so an unscoped IgnoreQueryFilters().First(JiraIssueId==...) can return another test's row under parallel runs.
        var story2 = await vdb.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "10002");
        var epic2 = await vdb.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "10001");
        Assert.Equal(epic2.Id, story2.ParentIssueId);
    }

    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string, object)[] ps)
    { await using var c = conn.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v); await c.ExecuteNonQueryAsync(); }
}

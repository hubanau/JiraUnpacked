using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt);

    /// <summary>The run as it ended up, read back after SyncWait saw it leave Running.</summary>
    private static async Task<SyncRunDto> FinishedRunAsync(HttpClient client, Guid projectId, Guid runId)
    {
        var runs = await client.GetFromJsonAsync<List<SyncRunDto>>($"/api/projects/{projectId}/sync-runs");
        return runs!.Single(r => r.Id == runId);
    }

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

    private void StubSearchTwoPages()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost().WithBody(b => b?.Contains("TOKEN2") != true))
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { issues = new object[] { new { id = "10001", key = "ACME-1" }, new { id = "10002", key = "ACME-2" } }, nextPageToken = "TOKEN2" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost().WithBody(b => b?.Contains("TOKEN2") == true))
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { issues = new object[] { new { id = "10003", key = "ACME-3" } } }));
    }

    private async Task<long> RawCountForTenantAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options; // privileged, bypass RLS for assertion
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        return await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
    }

    [Fact]
    public async Task Sync_lands_all_pages_and_records_a_succeeded_run()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();

        var resp = await SyncWait.SyncAsync(client, projectId);

        // The request is ACCEPTED, not completed: it hands the work to the background worker and
        // returns the Running row to poll. Asserting the eventual result therefore has to read
        // the run back rather than trust the response body — and asserting both halves is the
        // point, since "202 with a run that never finishes" would otherwise look like success.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;
        Assert.Equal("Running", accepted.Status);

        var run = await FinishedRunAsync(client, projectId, accepted.Id);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(3, run.IssuesProcessed);
        Assert.NotNull(run.NextRunAt);
        Assert.Equal(3, await RawCountForTenantAsync(projectId));
    }

    [Fact]
    public async Task Sync_is_idempotent_on_rerun()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();
        await SyncWait.SyncAsync(client, projectId);
        await SyncWait.SyncAsync(client, projectId);
        Assert.Equal(3, await RawCountForTenantAsync(projectId)); // upsert, not duplicate
    }

    [Fact]
    public async Task Second_run_filters_by_updated_watermark()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();
        await SyncWait.SyncAsync(client, projectId);
        _jira.Reset();
        StubValidAndProject();
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

        await SyncWait.SyncAsync(client, projectId);

        // The request body is JSON-escaped by System.Text.Json's default (HTML-safe) encoder,
        // so ">" is serialized as ">" on the wire; assert on the decoded "jql" field
        // rather than the raw string (same fix as EXT2's own search-body assertion, 32c782b).
        var searchCalls = _jira.LogEntries.Where(e => e.RequestMessage.Path == "/rest/api/3/search/jql").ToList();
        Assert.Contains(searchCalls, e =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!);
            return doc.RootElement.GetProperty("jql").GetString()!.Contains("updated >=");
        });
    }

    [Fact]
    public async Task Jira_failure_records_a_failed_run()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(500));

        var resp = await SyncWait.SyncAsync(client, projectId);
        var accepted = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;

        var run = await FinishedRunAsync(client, projectId, accepted.Id);
        Assert.Equal("Failed", run.Status);
        Assert.Equal(0, await RawCountForTenantAsync(projectId));
    }

    [Fact]
    public async Task Sync_unknown_project_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await SyncWait.SyncAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

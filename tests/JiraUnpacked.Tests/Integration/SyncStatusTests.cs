using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncStatusTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncStatusTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt);

    [Fact]
    public async Task Lists_runs_newest_first_after_a_sync()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[] { new { id = "10001", key = "ACME-1" } } }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        await SyncWait.SyncAsync(client, project.Id);

        var runs = await (await client.GetAsync($"/api/projects/{project.Id}/sync-runs")).Content.ReadFromJsonAsync<List<SyncRunDto>>();
        Assert.NotEmpty(runs!);
        Assert.Equal("Succeeded", runs![0].Status);
        Assert.Equal(1, runs[0].IssuesProcessed);
    }

    [Fact]
    public async Task Unknown_project_returns_empty_list()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var runs = await (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/sync-runs")).Content.ReadFromJsonAsync<List<SyncRunDto>>();
        Assert.Empty(runs!);
    }
}

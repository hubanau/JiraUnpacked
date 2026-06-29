// tests/JiraUnpacked.Tests/Integration/QueryEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class QueryEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public QueryEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record QueryDto(Guid Id, Guid JiraProjectId, string Jql, DateTime CreatedAtUtc);

    private async Task<(HttpClient client, Guid projectId)> WithProjectAsync()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        return (client, project.Id);
    }

    [Fact]
    public async Task Valid_jql_is_accepted()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/queries", new { jql = "project = ACME" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var q = (await resp.Content.ReadFromJsonAsync<QueryDto>())!;
        Assert.Equal("project = ACME", q.Jql);
    }

    [Fact]
    public async Task Invalid_jql_is_rejected_400()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { queries = new object[] { new { query = "bad", errors = new[] { "Unable to parse" } } } }));

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/queries", new { jql = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_to_unknown_project_returns_404()
    {
        var (client, _) = await WithProjectAsync();
        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/queries", new { jql = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

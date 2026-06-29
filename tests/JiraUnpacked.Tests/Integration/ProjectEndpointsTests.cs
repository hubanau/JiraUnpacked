// tests/JiraUnpacked.Tests/Integration/ProjectEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ProjectEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ProjectEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    private void StubValid() => _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));

    private async Task<(HttpClient client, Guid connId)> ConnectedAsync()
    {
        StubValid();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;
        return (client, conn.Id);
    }

    [Fact]
    public async Task Add_project_validates_the_key_and_captures_the_stable_id()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));

        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var p = (await resp.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal("10001", p.JiraProjectId);
        Assert.Equal(60, p.SyncIntervalMinutes);
    }

    [Fact]
    public async Task Unknown_project_key_is_rejected()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/NOPE").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(404));
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "NOPE", syncIntervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Interval_below_60_is_rejected()
    {
        var (client, connId) = await ConnectedAsync();
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicate_project_key_returns_409()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var body = new { projectKey = "ACME", syncIntervalMinutes = 60 };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
    }
}

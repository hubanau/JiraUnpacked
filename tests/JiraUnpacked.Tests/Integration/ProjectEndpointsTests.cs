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

    private void StubAcmeProject() =>
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));

    private async Task<(HttpClient client, Guid connId)> ConnectedAsync()
    {
        StubValid();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;
        return (client, conn.Id);
    }

    private async Task<ProjectDto> CreateAcmeProjectAsync(HttpClient client, Guid connId)
    {
        StubAcmeProject();
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    [Fact]
    public async Task Add_project_validates_the_key_and_captures_the_stable_id()
    {
        var (client, connId) = await ConnectedAsync();
        StubAcmeProject();

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
        StubAcmeProject();
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicate_project_key_returns_409()
    {
        var (client, connId) = await ConnectedAsync();
        StubAcmeProject();
        var body = new { projectKey = "ACME", syncIntervalMinutes = 60 };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
    }

    [Fact]
    public async Task Patch_updates_sync_interval()
    {
        var (client, connId) = await ConnectedAsync();
        var project = await CreateAcmeProjectAsync(client, connId);

        var patchResp = await client.PatchAsync($"/api/projects/{project.Id}",
            JsonContent.Create(new { syncIntervalMinutes = 120 }));
        Assert.Equal(HttpStatusCode.NoContent, patchResp.StatusCode);

        var getResp = await client.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var updated = (await getResp.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal(120, updated.SyncIntervalMinutes);
    }

    [Fact]
    public async Task Patch_below_60_is_rejected()
    {
        var (client, connId) = await ConnectedAsync();
        var project = await CreateAcmeProjectAsync(client, connId);

        var patchResp = await client.PatchAsync($"/api/projects/{project.Id}",
            JsonContent.Create(new { syncIntervalMinutes = 30 }));
        Assert.Equal(HttpStatusCode.BadRequest, patchResp.StatusCode);
    }

    [Fact]
    public async Task Patch_unknown_project_returns_404()
    {
        var (client, _) = await ConnectedAsync();

        var patchResp = await client.PatchAsync($"/api/projects/{Guid.NewGuid()}",
            JsonContent.Create(new { syncIntervalMinutes = 120 }));
        Assert.Equal(HttpStatusCode.NotFound, patchResp.StatusCode);
    }

    [Fact]
    public async Task List_returns_projects_for_connection()
    {
        var (client, connId) = await ConnectedAsync();
        var project = await CreateAcmeProjectAsync(client, connId);

        var listResp = await client.GetAsync($"/api/connections/{connId}/projects");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var projects = (await listResp.Content.ReadFromJsonAsync<List<ProjectDto>>())!;
        Assert.Contains(projects, p => p.JiraProjectKey == "ACME");
    }
}

using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ConnectionDiscoveryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ConnectionDiscoveryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record FieldDto(string Id, string Name, bool Custom);
    private sealed record ProjectSummaryDto(string Id, string Key, string Name);

    [Fact]
    public async Task Lists_jira_fields_for_a_connection()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
             { new { id = "customfield_10020", name = "Sprint", custom = true } }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;

        var fields = await (await client.GetAsync($"/api/connections/{conn.Id}/fields"))
            .Content.ReadFromJsonAsync<List<FieldDto>>();
        Assert.Contains(fields!, f => f.Id == "customfield_10020");
    }

    [Fact]
    public async Task Lists_jira_projects_for_a_connection()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/search").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                 new { values = new object[] { new { id = "10001", key = "ACME", name = "Acme" } } }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;

        var projects = await (await client.GetAsync($"/api/connections/{conn.Id}/projects-available"))
            .Content.ReadFromJsonAsync<List<ProjectSummaryDto>>();
        Assert.Contains(projects!, p => p.Key == "ACME");
    }

    [Fact]
    public async Task Unknown_connection_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await client.GetAsync($"/api/connections/{Guid.NewGuid()}/fields");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

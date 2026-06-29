using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class MappingEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public MappingEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record StatusDto(string Id, string Name);
    private sealed record FieldMappingDto(string Role, string JiraFieldId);
    private sealed record StatusSemanticDto(string JiraStatusId, string StatusName, string Class);

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
    public async Task Discovers_project_statuses()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME/statuses").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
             { new { id = "10", name = "Story", statuses = new object[] { new { id = "3", name = "Done" } } } }));

        var statuses = await (await client.GetAsync($"/api/projects/{projectId}/jira-statuses"))
            .Content.ReadFromJsonAsync<List<StatusDto>>();
        Assert.Contains(statuses!, s => s.Id == "3" && s.Name == "Done");
    }

    [Fact]
    public async Task Field_mappings_replace_and_read_back()
    {
        var (client, projectId) = await WithProjectAsync();
        var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/field-mappings", new
        {
            mappings = new[]
            {
                new { role = "Sprint", jiraFieldId = "customfield_10020" },
                new { role = "Status", jiraFieldId = "status" }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var read = await (await client.GetAsync($"/api/projects/{projectId}/field-mappings"))
            .Content.ReadFromJsonAsync<List<FieldMappingDto>>();
        Assert.Equal(2, read!.Count);
        Assert.Contains(read, m => m.Role == "Sprint" && m.JiraFieldId == "customfield_10020");

        // Replace semantics: PUT again with one mapping -> set becomes exactly that one.
        await client.PutAsJsonAsync($"/api/projects/{projectId}/field-mappings", new
        { mappings = new[] { new { role = "Assignee", jiraFieldId = "assignee" } } });
        var read2 = await (await client.GetAsync($"/api/projects/{projectId}/field-mappings"))
            .Content.ReadFromJsonAsync<List<FieldMappingDto>>();
        Assert.Single(read2!);
        Assert.Equal("Assignee", read2![0].Role);
    }

    [Fact]
    public async Task Status_semantics_persist()
    {
        var (client, projectId) = await WithProjectAsync();

        // First PUT: two items.
        var put1 = await client.PutAsJsonAsync($"/api/projects/{projectId}/status-semantics", new
        {
            statuses = new[]
            {
                new { jiraStatusId = "3", statusName = "Done", @class = "Done" },
                new { jiraStatusId = "1", statusName = "To Do", @class = "Active" }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, put1.StatusCode);

        var read1 = await (await client.GetAsync($"/api/projects/{projectId}/status-semantics"))
            .Content.ReadFromJsonAsync<List<StatusSemanticDto>>();
        Assert.Equal(2, read1!.Count);
        Assert.Contains(read1, s => s.JiraStatusId == "3" && s.Class == "Done");

        // Second PUT: disjoint single item — proves replace, not append.
        var put2 = await client.PutAsJsonAsync($"/api/projects/{projectId}/status-semantics", new
        {
            statuses = new[]
            {
                new { jiraStatusId = "99", statusName = "Archived", @class = "Other" }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, put2.StatusCode);

        var read2 = await (await client.GetAsync($"/api/projects/{projectId}/status-semantics"))
            .Content.ReadFromJsonAsync<List<StatusSemanticDto>>();
        Assert.Single(read2!);
        Assert.Contains(read2!, s => s.JiraStatusId == "99" && s.Class == "Other");
        Assert.DoesNotContain(read2!, s => s.JiraStatusId == "3");
    }
}

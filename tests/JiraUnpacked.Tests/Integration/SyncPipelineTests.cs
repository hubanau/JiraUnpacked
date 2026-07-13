using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncPipelineTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncPipelineTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    [Fact]
    public async Task Sync_lands_raw_and_produces_modeled_issue_and_changelog()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "10001", key = "ACME-1",
                    fields = new { summary = "S", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 1, histories = new object[] {
                        new { id = "h1", created = "2024-01-02T00:00:00.000+0000", items = new object[] { new { field = "status", toString = "Done" } } } } } },
                // Story ACME-2, parented to the epic above, to prove epic->story hierarchy resolves end-to-end via /sync.
                new { id = "10002", key = "ACME-2",
                    fields = new { summary = "T", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" }, parent = new { id = "10001", key = "ACME-1" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
            }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        await client.PutAsJsonAsync($"/api/projects/{project.Id}/field-mappings", new { mappings = new[] { new { role = "Status", jiraFieldId = "status" } } });

        var resp = await client.PostAsJsonAsync($"/api/projects/{project.Id}/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        Assert.Equal(2, await db.RawChangelogs.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "10001");
        Assert.NotNull(issue.StatusId);
        Assert.Contains(await db.IssueChangeHistory.IgnoreQueryFilters().Where(h => h.JiraProjectId == project.Id).ToListAsync(),
            h => h.Field == "status" && h.ToValue == "Done");

        // Epic<-story hierarchy resolved end-to-end via /sync (extract -> ingest -> derive), not seeded raw.
        Assert.Equal(2, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));
        var story = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "10002");
        Assert.Equal(issue.Id, story.ParentIssueId);
    }
}

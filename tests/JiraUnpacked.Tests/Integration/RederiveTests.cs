using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class RederiveTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public RederiveTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);

    private void StubConfig()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
    }

    private void StubSearchOneIssue()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             {
                 issues = new object[] { new {
                     id = "10001", key = "ACME-1",
                     fields = new {
                         summary = "First", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000",
                         issuetype = new { id = "1", name = "Story" },
                         priority = new { id = "3", name = "High" },
                         status = new { id = "10", name = "Done", statusCategory = new { name = "Done" } },
                         assignee = new { accountId = "u1", displayName = "Alice" }
                     },
                     changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] }
                 } }
             }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredProjectAsync()
    {
        StubConfig();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        // Map status + assignee to the standard fields so derivation resolves them.
        await client.PutAsJsonAsync($"/api/projects/{project.Id}/field-mappings", new { mappings = new[] {
            new { role = "Status", jiraFieldId = "status" }, new { role = "Assignee", jiraFieldId = "assignee" } } });
        return (client, project.Id);
    }

    private async Task<Domain.Entities.Issue?> IssueAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        return await db.Issues.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.JiraProjectId == projectId);
    }

    private async Task<int> IssueCountAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        return await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == projectId);
    }

    [Fact]
    public async Task Rederive_builds_issues_and_dictionaries_from_raw_without_calling_jira()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchOneIssue();
        await SyncWait.SyncAsync(client, projectId); // lands raw (+ derives once TR6 wires it)

        _jira.Reset(); // any Jira call during rederive would now 404
        // Accepted, then performed by the background worker — so the terminal status is read
        // back from the runs list rather than from the response body, which shows it Running.
        var resp = await SyncWait.RederiveAsync(client, projectId);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;

        var runs = await client.GetFromJsonAsync<List<SyncRunDto>>($"/api/projects/{projectId}/sync-runs");
        var run = runs!.Single(r => r.Id == accepted.Id);
        Assert.Equal("Rederive", run.Kind);
        Assert.Equal("Succeeded", run.Status);
        Assert.Empty(_jira.LogEntries); // zero Jira calls on rederive

        var issue = await IssueAsync(projectId);
        Assert.NotNull(issue);
        Assert.Equal("ACME-1", issue!.JiraKey);
        Assert.Equal("First", issue.Summary);
        Assert.NotNull(issue.StatusId);    // resolved via the status dict
        Assert.NotNull(issue.IssueTypeId); // standard issuetype dict
        Assert.NotNull(issue.PriorityId);
        Assert.NotNull(issue.AssigneeId);
    }

    [Fact]
    public async Task Rederive_twice_does_not_duplicate_issues()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchOneIssue();
        await SyncWait.SyncAsync(client, projectId); // lands raw
        await SyncWait.RederiveAsync(client, projectId);
        await SyncWait.RederiveAsync(client, projectId);
        Assert.Equal(1, await IssueCountAsync(projectId)); // upsert by (EndUserId, JiraIssueId), not duplicated
    }

    [Fact]
    public async Task Rederive_unknown_project_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await SyncWait.RederiveAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

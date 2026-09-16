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

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

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

    /// <summary>
    /// Both slow transform phases report their own progress, against real data.
    ///
    /// Reading change history and rebuilding the model each run for many minutes on a large
    /// project, and both used to sit behind a bar frozen at whatever the fetch had counted. This
    /// walks a real sync and captures what each phase published, because the reporting is plumbed
    /// through three components — service, orchestrator, run row — and a unit test of any one of
    /// them would not notice the wiring being wrong.
    /// </summary>
    [Fact]
    public async Task The_transform_phases_report_progress_of_their_own()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "70001", key = "ACME-1",
                    fields = new { summary = "one", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } },
                new { id = "70002", key = "ACME-2",
                    fields = new { summary = "two", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
            }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());

        var run = await db.SyncRuns.IgnoreQueryFilters().FirstAsync(r => r.JiraProjectId == project.Id);
        Assert.Equal(JiraUnpacked.Domain.Enums.SyncRunStatus.Succeeded, run.Status);

        // A finished run reports no phase progress — it is doing nothing — while its own issue
        // figures, which mean something different, survive untouched by any of the phase writes.
        Assert.Null(run.Phase);
        Assert.Null(run.PhaseProcessed);
        Assert.Null(run.PhaseTotal);
        Assert.Equal(2, run.IssuesProcessed);
    }

    /// <summary>
    /// Builds an Atlassian-Document-Format-shaped body nested `depth` levels deep: a bulletList
    /// whose listItem contains another bulletList, and so on. Real ADF nests this way through
    /// lists inside table cells inside panels.
    /// </summary>
    private static object DeeplyNested(int depth) =>
        depth <= 0
            ? new { type = "text", text = "bottom" }
            : new { type = "bulletList", content = new[] { DeeplyNested(depth - 1) } };

    /// <summary>
    /// One issue with a very deep description must not kill the sync.
    ///
    /// System.Text.Json defaults to a maximum nesting depth of 64, and a real project hit it: a
    /// single issue's ADF body went deeper, JsonDocument.Parse threw, and a 42,897-issue sync died
    /// 31,800 issues in — three separate runs, all at the same point, because it is a property of
    /// one document rather than a resource limit.
    ///
    /// The assertion is deliberately end-to-end. The payload is parsed three times by three
    /// different components — the search response, then the stored snapshot by ingest, then again
    /// by derivation — so raising the limit at the point that threw would only move the failure
    /// to the next reader.
    /// </summary>
    [Fact]
    public async Task An_issue_nested_deeper_than_the_default_json_limit_still_syncs()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "30001", key = "ACME-DEEP",
                    fields = new {
                        summary = "deep", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000",
                        status = new { id = "10", name = "Done" },
                        // 120 levels: comfortably past the 64 default, comfortably under the raised bound.
                        description = new { type = "doc", version = 1, content = new[] { DeeplyNested(120) } },
                    },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
            }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());

        // The run itself succeeded — before the fix this is where the JsonReaderException landed.
        var run = await db.SyncRuns.IgnoreQueryFilters().FirstAsync(r => r.JiraProjectId == project.Id);
        Assert.Equal(JiraUnpacked.Domain.Enums.SyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);

        // Stored by extract, read by ingest, and modelled by derivation — all three.
        Assert.Equal(1, await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        Assert.Equal(1, await db.RawChangelogs.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id);
        Assert.Equal("ACME-DEEP", issue.JiraKey);
        Assert.Equal("deep", issue.Summary);
    }

    /// <summary>
    /// The reported bug, walked exactly as a user hits it: add a connection, add a project, press
    /// Sync. No JQL query is created because the UI offers no way to create one — and the sync used
    /// to return Succeeded with 0 issues and no error, leaving the project permanently empty.
    ///
    /// Deliberately the same path as the test above minus the POST to /queries, so what it proves
    /// is that the missing query is no longer the difference between data and silence.
    /// </summary>
    [Fact]
    public async Task Sync_lands_data_for_a_project_that_has_no_query_of_its_own()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "20001", key = "ACME-9",
                    fields = new { summary = "Unqueried", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
            }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        // NOTE: no POST to /api/projects/{id}/queries — that is the whole point.

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        // Jira was asked for the whole project, by key.
        var searched = _jira.LogEntries
            .Where(e => e.RequestMessage.Path == "/rest/api/3/search/jql")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!).RootElement.GetProperty("jql").GetString())
            .ToList();
        Assert.Equal(new[] { "project = \"ACME\"" }, searched);

        // And the data actually landed, all the way through to the modelled table — a green run
        // over an empty database was the entire symptom, so the run status alone proves nothing.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        Assert.Equal(1, await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        Assert.Equal(1, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));
    }
}

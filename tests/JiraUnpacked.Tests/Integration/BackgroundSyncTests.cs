// tests/JiraUnpacked.Tests/Integration/BackgroundSyncTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Sync is handed to a background worker rather than performed inside the HTTP request.
///
/// The reason is a real failure: a project with ~43,000 issues takes minutes to pull, the request
/// ran the whole pipeline inline, and its CancellationToken was the request's — so a browser that
/// gave up waiting cancelled the sync it had just started, every time, and the project could
/// never finish a first pull.
/// </summary>
public sealed class BackgroundSyncTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public BackgroundSyncTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record RunDto(Guid Id, string Status, string Kind, int IssuesProcessed);

    private void StubSite(TimeSpan searchDelay)
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithDelay(searchDelay).WithBodyAsJson(new
             {
                 issues = new object[] {
                     new { id = "10001", key = "ACME-1",
                         fields = new { summary = "S", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000" },
                         changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
                 }
             }));
    }

    private async Task<(HttpClient client, Guid projectId)> ProjectAsync(TimeSpan searchDelay)
    {
        StubSite(searchDelay);
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        return (client, project.Id);
    }

    [Fact]
    public async Task The_request_returns_while_the_work_is_still_going()
    {
        // Jira is held for two seconds. If the endpoint still performed the sync inline, the POST
        // could not possibly come back before that — so the timing IS the assertion here.
        var (client, projectId) = await ProjectAsync(TimeSpan.FromSeconds(2));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        clock.Stop();

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
            $"the request took {clock.Elapsed.TotalSeconds:N1}s, so it waited for the sync instead of handing it off");

        var accepted = (await resp.Content.ReadFromJsonAsync<RunDto>())!;
        Assert.Equal("Running", accepted.Status);

        // And it genuinely finishes afterwards — 202 for work nobody performs would be worse
        // than the inline version it replaced.
        await SyncWait.WaitForRunAsync(client, projectId, accepted.Id);
        var runs = await client.GetFromJsonAsync<List<RunDto>>($"/api/projects/{projectId}/sync-runs");
        Assert.Equal("Succeeded", runs!.Single(r => r.Id == accepted.Id).Status);
    }

    [Fact]
    public async Task Abandoning_the_request_does_not_cancel_the_run()
    {
        // The exact production failure. The caller gives up after 200ms on a sync that needs a
        // second; the run must still complete, because the work no longer holds the request's
        // cancellation token.
        var (client, projectId) = await ProjectAsync(TimeSpan.FromSeconds(1));

        Guid runId;
        using (var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { }, giveUp.Token);
            runId = (await resp.Content.ReadFromJsonAsync<RunDto>(cancellationToken: giveUp.Token))!.Id;
        }

        // Observed through the SAME client, deliberately: the point is that abandoning one
        // request did not poison the run, not that a second client can see it.
        await SyncWait.WaitForRunAsync(client, projectId, runId);

        var runs = await client.GetFromJsonAsync<List<RunDto>>($"/api/projects/{projectId}/sync-runs");
        var run = runs!.Single(r => r.Id == runId);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, run.IssuesProcessed);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        Assert.Equal(1, await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId));
    }

    [Fact]
    public async Task A_second_press_while_one_is_running_is_a_conflict()
    {
        // The one-running-per-project rule still holds now that the answer comes back before the
        // work does — otherwise the queue would happily stack duplicate pulls of the same project.
        var (client, projectId) = await ProjectAsync(TimeSpan.FromSeconds(2));

        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await SyncWait.WaitForQuietAsync(client, projectId);
    }

    [Fact]
    public async Task An_unknown_project_is_still_rejected_rather_than_accepted()
    {
        // The claim stays synchronous precisely so this keeps working: an endpoint that only
        // enqueued could answer nothing but "accepted", including for a project that does not exist.
        var client = await TestData.AuthedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/sync", new { });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

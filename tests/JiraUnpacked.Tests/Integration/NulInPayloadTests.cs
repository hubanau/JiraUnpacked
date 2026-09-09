// tests/JiraUnpacked.Tests/Integration/NulInPayloadTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A NUL in one issue's text used to end the whole sync.
///
/// PostgreSQL cannot store NUL in json or jsonb — jsonb becomes text and text cannot represent
/// it — so the INSERT for the page carrying that issue is rejected with 22P05. Jira accepts a NUL
/// pasted into a text field and serves it back escaped, so this is ordinary data, not corruption.
/// A real 42,918-issue run died on one.
///
/// This runs against real Postgres deliberately: the whole defect is a database constraint that
/// no in-memory provider has, and a stubbed store would pass while production failed.
/// </summary>
public sealed class NulInPayloadTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public NulInPayloadTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    [Fact]
    public async Task An_issue_whose_text_contains_a_NUL_still_syncs()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        // The summary carries a real NUL. Serialised to JSON it becomes the six-character escape
        // that Postgres refuses — the same shape as the issue that killed the production run.
        var withNul = "Date/Time of Incident: - June 22, 8:53 am" + (char)0 + " (reported)";

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "40001", key = "ACME-NUL",
                    fields = new { summary = withNul, created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } },
                // A second, ordinary issue on the same page: the rejected INSERT took the whole
                // batch with it, so a neighbour being lost is part of the defect.
                new { id = "40002", key = "ACME-OK",
                    fields = new { summary = "ordinary", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
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
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);

        // Both issues stored and modelled — the NUL one is kept, not skipped: losing a real issue
        // over an invisible character would be its own defect.
        Assert.Equal(2, await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        Assert.Equal(2, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));

        // The text survives with only the NUL gone.
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraIssueId == "40001");
        Assert.Equal("Date/Time of Incident: - June 22, 8:53 am (reported)", issue.Summary);
        Assert.DoesNotContain('\0', issue.Summary!);
    }
}

// tests/JiraUnpacked.Tests/Integration/SyncCollisionTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A tenant with two Jira connections whose sites share issue id "10001" must end up with two
/// independent rows, not one project's data overwriting the other's. This test FAILS on the
/// pre-fix schema (the tenant-wide unique key + unscoped lookup collapse them into one row).
/// </summary>
public sealed class SyncCollisionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jiraA = WireMockServer.Start();
    private readonly WireMockServer _jiraB = WireMockServer.Start();
    public SyncCollisionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _jiraA.Stop(); _jiraB.Stop(); }

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    // Stubs one WireMock server as a Jira site whose project <key> contains a single issue
    // "10001" with the given summary, plus the connection-validation + jql-parse endpoints.
    private static void StubSite(WireMockServer jira, string key, string summary)
    {
        jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        jira.Given(Request.Create().WithPath($"/rest/api/3/project/{key}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key, name = key }));
        jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = $"project = {key}", errors = new string[0] } } }));
        jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10001", key = $"{key}-1",
                    fields = new { summary, created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-01T00:00:00.000+0000" },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } } }
            }));
    }

    private async Task<Guid> ConfigureAndSyncAsync(HttpClient client, WireMockServer jira, string key, string summary)
    {
        StubSite(jira, key, summary);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = key, syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = $"project = {key}" });
        await SyncWait.SyncAsync(client, project.Id);
        return project.Id;
    }

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public async Task Two_connections_sharing_an_issue_id_do_not_collide()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var projectA = await ConfigureAndSyncAsync(client, _jiraA, "ACME", "Acme's 10001");
        var projectB = await ConfigureAndSyncAsync(client, _jiraB, "BETA", "Beta's 10001");

        await using var db = Privileged();

        // Two independent raw snapshots, each under its own project, each with its own payload.
        var snaps = await db.RawIssueSnapshots.IgnoreQueryFilters()
            .Where(r => r.JiraProjectId == projectA || r.JiraProjectId == projectB)
            .ToListAsync();
        Assert.Equal(2, snaps.Count);
        Assert.Contains(snaps, s => s.JiraProjectId == projectA && s.Payload.Contains("Acme's 10001"));
        Assert.Contains(snaps, s => s.JiraProjectId == projectB && s.Payload.Contains("Beta's 10001"));

        // Two independent modeled issues, each under its own project.
        var issues = await db.Issues.IgnoreQueryFilters()
            .Where(i => i.JiraProjectId == projectA || i.JiraProjectId == projectB)
            .ToListAsync();
        Assert.Equal(2, issues.Count);
        Assert.Equal("Acme's 10001", Assert.Single(issues, i => i.JiraProjectId == projectA).Summary);
        Assert.Equal("Beta's 10001", Assert.Single(issues, i => i.JiraProjectId == projectB).Summary);
    }
}

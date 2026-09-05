// tests/JiraUnpacked.Tests/Integration/RederiveWatermarkTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

// Proves the Critical fix (final review): a Rederive run must NOT advance the
// incremental-sync watermark used by SyncOrchestrator.RunProjectAsync. Only the
// most recent Kind=Sync, Status=Succeeded run over the SAME SCOPE should seed the
// "updated >= ..." clause.
public sealed class RederiveWatermarkTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public RederiveWatermarkTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    private void StubValidAndProject()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredProjectAsync()
    {
        StubValidAndProject();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        return (client, project.Id);
    }

    // Privileged read (bypasses RLS) to resolve the tenant that owns the freshly-configured
    // project, so we can seed SyncRuns rows for it directly via SQL.
    private async Task<Guid> ResolveTenantAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var project = await db.JiraProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId);
        return project.EndUserId;
    }

    private static async Task SeedSyncRunAsync(Guid tenant, Guid projId, string kind, string status,
        DateTime startedAt, DateTime? finishedAt, string? scopeFingerprint)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""SyncRuns""
            (""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""FinishedAt"",""Status"",""IssuesProcessed"",""Kind"",""ScopeFingerprint"")
            VALUES (@id,@t,@proj,@started,@finished,@status,0,@kind,@scope)";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("proj", projId);
        cmd.Parameters.AddWithValue("started", startedAt);
        cmd.Parameters.AddWithValue("finished", finishedAt.HasValue ? (object)finishedAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("scope", (object?)scopeFingerprint ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// When the project's sync run started AND the scope it recorded, read privileged (bypasses
    /// RLS). The fingerprint is taken from the real run rather than recomputed here: copying the
    /// hashing onto the seeded Rederive row would only prove the test can mirror the
    /// implementation, and a Rederive carrying a DIFFERENT scope would be filtered out for that
    /// reason instead of for being a Rederive — leaving this test unable to fail.
    /// </summary>
    private static async Task<(DateTime StartedAt, string? Scope)> FirstSyncRunAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var run = await db.SyncRuns.IgnoreQueryFilters()
            .Where(r => r.JiraProjectId == projectId && r.Kind == Domain.Enums.SyncRunKind.Sync)
            .OrderBy(r => r.StartedAt)
            .FirstAsync();
        return (run.StartedAt, run.ScopeFingerprint);
    }

    [Fact]
    public async Task Rederive_succeeded_run_does_not_advance_the_sync_watermark()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        var tenant = await ResolveTenantAsync(projectId);

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             {
                 issues = new object[] { new { id = "10001", key = "ACME-1", fields = new { summary = "S" } } }
             }));

        // The watermark is established by RUNNING a sync, not by seeding a row. A hand-seeded run
        // cannot record which SCOPE it covered, and a run whose scope is unknown is no longer
        // allowed to seed a watermark — so seeding one would leave this test proving nothing about
        // Rederive, which is how it started failing when scope fingerprints arrived.
        (await SyncWait.SyncAsync(client, projectId)).EnsureSuccessStatusCode();
        var (syncedAt, scope) = await FirstSyncRunAsync(projectId);
        Assert.NotNull(scope);

        // A LATER rederive over the SAME scope, which must not become the watermark despite being
        // the most recent successful run. Same scope on purpose: Kind has to be the only thing
        // separating it from the sync, or this test cannot detect Kind being ignored.
        var rederiveAt = syncedAt.AddDays(1);
        await SeedSyncRunAsync(tenant, projectId, "Rederive", "Succeeded", rederiveAt, rederiveAt.AddMinutes(1), scope);

        _jira.ResetLogEntries();
        (await SyncWait.SyncAsync(client, projectId)).EnsureSuccessStatusCode();

        var jqls = _jira.LogEntries
            .Where(e => e.RequestMessage.Path == "/rest/api/3/search/jql")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!)
                .RootElement.GetProperty("jql").GetString()!)
            .ToList();

        var fromSync = $"updated >= \"{syncedAt:yyyy-MM-dd HH:mm}\"";
        var fromRederive = $"{rederiveAt:yyyy-MM-dd HH:mm}";
        Assert.Contains(jqls, j => j.Contains(fromSync));
        Assert.DoesNotContain(jqls, j => j.Contains(fromRederive));
    }
}

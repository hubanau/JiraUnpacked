using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class RederiveErrorTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public RederiveErrorTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record RunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rederive_over_malformed_raw_persists_a_generic_error_not_raw_detail()
    {
        // WireMock only for connection/config calls; no sync — we seed raw directly then rederive.
        using var jira = WireMock.Server.WireMockServer.Start();
        jira.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/3/myself").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        jira.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        // Resolve tenant, seed a snapshot whose Payload is invalid JSON.
        Guid tenant;
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using (var db = new AppDbContext(opts, new NullCurrentTenant()))
            tenant = await db.JiraProjects.IgnoreQueryFilters().Where(p => p.Id == project.Id).Select(p => p.EndUserId).FirstAsync();

        await using (var raw = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await raw.OpenAsync();
            // Payload is jsonb (other tests cast with ::jsonb), so it must be valid JSON to insert.
            // A bare number '123' is valid jsonb: JsonDocument.Parse succeeds, then DeriveProjectAsync's
            // root.TryGetProperty("fields", ...) throws InvalidOperationException (root is a Number, not
            // an Object) — a non-JiraApiException, which is exactly the leak path under test.
            await Exec(raw, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001',@pay::jsonb,now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("pay", "123"));
        }

        await SyncWait.RederiveAsync(client, project.Id);

        var runs = await (await client.GetAsync($"/api/projects/{project.Id}/sync-runs")).Content.ReadFromJsonAsync<List<RunDto>>();
        var rederive = Assert.Single(runs!, r => r.Kind == "Rederive");
        Assert.Equal("Failed", rederive.Status);
        Assert.Equal("Sync failed unexpectedly.", rederive.Error);   // generic, not the raw JsonException text
    }
}

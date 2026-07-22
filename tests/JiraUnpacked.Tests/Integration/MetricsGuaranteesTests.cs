// tests/JiraUnpacked.Tests/Integration/MetricsGuaranteesTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The two guarantees FR-E2 exists for: tenant isolation on every metrics route, and
/// "editing the status mapping changes the numbers with zero Jira calls".
/// NOTE: this file starts no WireMock server — that absence IS the zero-Jira proof.
/// </summary>
public sealed class MetricsGuaranteesTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsGuaranteesTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TimeInProgressDto(double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Routes =
    {
        "story-counts", "time-in-progress", "throughput", "status-aging",
    };

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_metrics()
    {
        var (_, tenantA) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenantA);
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 3m, Jan(1));

        var (clientB, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        // B asks for A's project id on every route -> filtered out -> 404, no existence leak.
        foreach (var route in Routes)
        {
            var resp = await clientB.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/{route}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Unknown_project_returns_404_on_every_route()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        var missing = Guid.NewGuid();

        foreach (var route in Routes)
        {
            var resp = await client.GetAsync($"/api/projects/{missing}/metrics/{route}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Editing_the_status_mapping_changes_the_numbers_without_touching_jira()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // "To Do" (Other) -> "In Progress" (Active) on Jan 2 -> "Done" (Done) on Jan 5.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h2", "In Progress", "Done", Jan(5));

        var before = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<TimeInProgressDto>();
        Assert.Equal(3.0, before!.AverageDays!.Value, 3);   // Jan 2 -> Jan 5

        // Reclassify "To Do" (JiraStatusId "1") from Other to Active: the issue now becomes
        // Active at CREATION (Jan 1), so the cycle time grows to 4 days. No sync, no Jira call.
        await MetricsTestData.SetSemanticClassAsync(ctx, "1", "Active");

        var after = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<TimeInProgressDto>();
        Assert.Equal(4.0, after!.AverageDays!.Value, 3);    // Jan 1 -> Jan 5

        Assert.NotEqual(before.AverageDays!.Value, after.AverageDays!.Value);
    }
}

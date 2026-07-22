// tests/JiraUnpacked.Tests/Integration/MetricsThroughputTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsThroughputTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsThroughputTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record BucketDto(DateTime PeriodStart, int Completed);
    private sealed record ReportDto(string Bucket, DateTime From, DateTime To, List<BucketDto> Buckets);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Buckets_completions_by_day_and_zero_fills_gaps()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Two issues done on Jan 3, one on Jan 5. Jan 4 must appear as an explicit zero.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "Done", Jan(3));
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h2", "To Do", "Done", Jan(3));
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h3", "To Do", "Done", Jan(5));

        var r = await (await client.GetAsync(
                $"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=day&from=2024-01-03&to=2024-01-05"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal("day", r!.Bucket);
        Assert.Equal(3, r.Buckets.Count);                       // Jan 3, 4, 5 — dense
        Assert.Equal(2, r.Buckets[0].Completed);
        Assert.Equal(0, r.Buckets[1].Completed);                // zero-filled gap
        Assert.Equal(1, r.Buckets[2].Completed);
        Assert.Equal(Jan(3), r.Buckets[0].PeriodStart);
    }

    [Fact]
    public async Task Week_buckets_snap_outward_to_iso_monday()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Jan 3 2024 is a Wednesday; its ISO week starts Monday Jan 1.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "Done", Jan(3));

        var r = await (await client.GetAsync(
                $"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=week&from=2024-01-03&to=2024-01-03"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Single(r!.Buckets);
        Assert.Equal(Jan(1), r.Buckets[0].PeriodStart);   // snapped back to Monday
        Assert.Equal(1, r.Buckets[0].Completed);
    }

    [Fact]
    public async Task Invalid_bucket_returns_400()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=fortnight");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Inverted_date_range_returns_400()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=day&from=2024-01-31&to=2024-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

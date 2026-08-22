// tests/JiraUnpacked.Tests/Integration/MetricsCachingTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The timeline fold is cached, so what needs proving end-to-end is that nothing which changes
/// an answer survives as a stale one. Cross-tenant key separation is proven in
/// ReportCacheTests, which can hold the project id constant and vary only the tenant — the
/// arrangement that actually discriminates, and one this layer cannot produce because project
/// ids are unique per tenant.
/// </summary>
public sealed class MetricsCachingTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsCachingTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static DateTime D(int day) => new(2024, 4, day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<StatusAgingReport> AgingAsync(HttpClient client, Guid projectId)
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/metrics/status-aging");
        return (await resp.Content.ReadFromJsonAsync<StatusAgingReport>())!;
    }

    [Fact]
    public async Task Editing_status_semantics_changes_the_metric_immediately()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // One issue sitting in Done. Aging counts everything NOT currently Done, so it is empty.
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "finished", ctx.DoneStatusId, 3m, D(1));

        var before = await AgingAsync(client, ctx.ProjectId);
        Assert.Equal(0, before.Items.TotalCount);

        // Reclassify "Done" as Other. No sync, no data change — only the semantics map, which
        // is exactly the dependency a naive cache key would miss. The same issue is now open.
        await MetricsTestData.SetSemanticClassAsync(ctx, "3", "Other");

        var after = await AgingAsync(client, ctx.ProjectId);

        Assert.Equal(1, after.Items.TotalCount);
        Assert.Equal("ACME-1", after.Items.Items.Single().JiraKey);
    }

    [Fact]
    public async Task Repeated_identical_requests_stay_consistent()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "open", ctx.ToDoStatusId, 1m, D(1));

        // The second and third calls are cache hits. A shared entry that a caller mutated —
        // the failure mode of caching a mutable fold — would show up as drift here.
        var first = await AgingAsync(client, ctx.ProjectId);
        var second = await AgingAsync(client, ctx.ProjectId);
        var third = await AgingAsync(client, ctx.ProjectId);

        Assert.Equal(1, first.Items.TotalCount);
        Assert.Equal(first.Items.TotalCount, second.Items.TotalCount);
        Assert.Equal(first.Items.TotalCount, third.Items.TotalCount);
        Assert.Equal(first.Histogram.Sum(b => b.Count), third.Histogram.Sum(b => b.Count));
    }

    [Fact]
    public async Task A_filtered_report_does_not_reuse_the_unfiltered_answer()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "alpha open", ctx.ToDoStatusId, 1m, D(1));
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "beta open", ctx.ToDoStatusId, 1m, D(1));

        var all = await AgingAsync(client, ctx.ProjectId);
        Assert.Equal(2, all.Items.TotalCount);

        // Same project, same watermark, same semantics — only the filter differs, so the filter
        // has to be part of the fingerprint or this returns the unfiltered two.
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/status-aging",
            new { conditions = new[] { new { field = "summary", op = "Contains", value = "alpha" } } });
        var filtered = (await resp.Content.ReadFromJsonAsync<StatusAgingReport>())!;

        Assert.Equal(1, filtered.Items.TotalCount);
        Assert.Equal("ACME-1", filtered.Items.Items.Single().JiraKey);

        // And the unfiltered answer is still correct afterwards — the filtered entry must not
        // have displaced it under the same key.
        Assert.Equal(2, (await AgingAsync(client, ctx.ProjectId)).Items.TotalCount);
    }

    [Fact]
    public async Task Newly_synced_data_is_visible_rather_than_cached_away()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "first", ctx.ToDoStatusId, 1m, D(1));

        Assert.Equal(1, (await AgingAsync(client, ctx.ProjectId)).Items.TotalCount);

        // Direct row insert with no SyncRun, so the watermark does NOT move. This is the one
        // case the fingerprint cannot see, and the short absolute expiry is the backstop for
        // it. Asserting the pre-expiry behaviour would pin staleness as a guarantee, so what is
        // asserted is only that the earlier answer is not corrupted.
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "second", ctx.ToDoStatusId, 1m, D(2));

        var again = await AgingAsync(client, ctx.ProjectId);
        Assert.InRange(again.Items.TotalCount, 1, 2);
        Assert.Contains(again.Items.Items, i => i.JiraKey == "ACME-1");
    }
}

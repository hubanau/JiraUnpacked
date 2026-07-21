// tests/JiraUnpacked.Tests/Integration/MetricsStatusAgingTests.cs
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsStatusAgingTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsStatusAgingTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record AgingItemDto(string JiraIssueId, string JiraKey, string? Summary,
        string? CurrentStatus, string Class, double AgeDays, DateTime EnteredAt);
    private sealed record BandDto(string Band, int Count);
    private sealed record PageDto(List<AgingItemDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ReportDto(PageDto Items, List<BandDto> Histogram);

    [Fact]
    public async Task Lists_open_issues_oldest_first_with_histogram_over_whole_population()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        // One open 10 days (band "1-2w" = [7,14)), one open 2 days (band "1-3d" = [1,3)),
        // and one Done issue that must NOT appear at all.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "old", ctx.InProgressStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", now.AddDays(-10));

        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "new", ctx.InProgressStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h2", "To Do", "In Progress", now.AddDays(-2));

        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "done", ctx.DoneStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h3", "To Do", "Done", now.AddDays(-1));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/status-aging"))
            .Content.ReadFromJsonAsync<ReportDto>();

        // Done issues are excluded from the aging population.
        Assert.Equal(2, r!.Items.TotalCount);
        Assert.Equal(2, r.Items.Items.Count);

        // Oldest first.
        Assert.Equal("ACME-1", r.Items.Items[0].JiraKey);
        Assert.Equal("ACME-2", r.Items.Items[1].JiraKey);
        Assert.Equal(10.0, r.Items.Items[0].AgeDays, 1);
        Assert.Equal(2.0, r.Items.Items[1].AgeDays, 1);
        Assert.Equal("In Progress", r.Items.Items[0].CurrentStatus);
        Assert.Equal("Active", r.Items.Items[0].Class);

        // All five bands always emitted; the 10-day issue lands in "1-2w", the 2-day in "1-3d".
        Assert.Equal(5, r.Histogram.Count);
        Assert.Equal(1, Assert.Single(r.Histogram, b => b.Band == "1-2w").Count);
        Assert.Equal(1, Assert.Single(r.Histogram, b => b.Band == "1-3d").Count);
        Assert.Equal(0, Assert.Single(r.Histogram, b => b.Band == ">2w").Count);
    }

    [Fact]
    public async Task Histogram_covers_the_whole_population_not_just_the_page()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        // Three open issues, all ~2 days old; ask for a single-item page.
        for (var n = 1; n <= 3; n++)
        {
            await MetricsTestData.SeedIssueAsync(ctx, $"1000{n}", $"ACME-{n}", "x", ctx.InProgressStatusId, null, now.AddDays(-30));
            await MetricsTestData.SeedStatusChangeAsync(ctx, $"1000{n}", $"h{n}", "To Do", "In Progress", now.AddDays(-2));
        }

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/status-aging?page=1&pageSize=1"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Single(r!.Items.Items);          // page holds one
        Assert.Equal(3, r.Items.TotalCount);    // population is three
        Assert.Equal(3, Assert.Single(r.Histogram, b => b.Band == "1-3d").Count); // histogram counts all three
    }
}

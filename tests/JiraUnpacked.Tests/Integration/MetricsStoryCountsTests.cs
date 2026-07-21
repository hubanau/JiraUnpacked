// tests/JiraUnpacked.Tests/Integration/MetricsStoryCountsTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsStoryCountsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsStoryCountsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record StatusCountDto(string? Status, string Class, int Count, decimal StoryPoints);
    private sealed record ReportDto(List<StatusCountDto> ByStatus, int TotalIssues, decimal TotalStoryPoints);

    private static readonly DateTime Jan1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Groups_issues_by_current_status_with_classes_and_point_sums()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // 2 In Progress (3 + 5 points), 1 Done (2 points), 1 with no status at all.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 3m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.InProgressStatusId, 5m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, 2m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10004", "ACME-4", "d", null, null, Jan1);

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/story-counts"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal(4, r!.TotalIssues);
        Assert.Equal(10m, r.TotalStoryPoints);

        var wip = Assert.Single(r.ByStatus, s => s.Status == "In Progress");
        Assert.Equal("Active", wip.Class);
        Assert.Equal(2, wip.Count);
        Assert.Equal(8m, wip.StoryPoints);

        var done = Assert.Single(r.ByStatus, s => s.Status == "Done");
        Assert.Equal("Done", done.Class);
        Assert.Equal(1, done.Count);

        // The status-less issue groups under a null status, classified Other.
        var none = Assert.Single(r.ByStatus, s => s.Status is null);
        Assert.Equal("Other", none.Class);
        Assert.Equal(1, none.Count);
        Assert.Equal(0m, none.StoryPoints);
    }

    [Fact]
    public async Task Empty_project_returns_200_with_zeros()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/story-counts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 200, NOT 404

        var r = await resp.Content.ReadFromJsonAsync<ReportDto>();
        Assert.Equal(0, r!.TotalIssues);
        Assert.Equal(0m, r.TotalStoryPoints);
        Assert.Empty(r.ByStatus);
    }
}

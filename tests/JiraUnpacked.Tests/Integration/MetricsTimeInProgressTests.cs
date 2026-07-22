// tests/JiraUnpacked.Tests/Integration/MetricsTimeInProgressTests.cs
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsTimeInProgressTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsTimeInProgressTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ReportDto(double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Averages_cycle_time_over_completed_issues_only()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // A: Active Jan2 -> Done Jan5  => 3 days.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, 3m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h2", "In Progress", "Done", Jan(5));

        // B: Active Jan2 -> Done Jan7  => 5 days. Average of A and B = 4 days.
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.DoneStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h3", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h4", "In Progress", "Done", Jan(7));

        // C: still In Progress -> no FirstDoneAt -> excluded.
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.InProgressStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h5", "To Do", "In Progress", Jan(3));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal(4.0, r!.AverageDays!.Value, 3);
        Assert.Equal(2, r.CompletedIssuesConsidered);
        Assert.Equal(1, r.IssuesExcluded);
    }

    [Fact]
    public async Task Average_is_null_when_no_issue_qualifies()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Never reached Done.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Null(r!.AverageDays);           // null, not 0 — 0 would be a lie
        Assert.Equal(0, r.CompletedIssuesConsidered);
        Assert.Equal(1, r.IssuesExcluded);
    }
}

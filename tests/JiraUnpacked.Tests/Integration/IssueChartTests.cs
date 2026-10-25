// tests/JiraUnpacked.Tests/Integration/IssueChartTests.cs
using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The list's chart. What matters is that it describes exactly the issues the list shows — same
/// filter, same total — grouped by the names a reader sees, with (Empty) a group of its own.
/// Every test seeds a fresh project and reads only through it.
/// </summary>
public sealed class IssueChartTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueChartTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record GroupDto(string? Key, int Issues, decimal StoryPoints);
    private sealed record OtherDto(int Groups, int Issues, decimal StoryPoints);
    private sealed record ChartDto(
        string GroupBy, int TotalIssues, decimal TotalStoryPoints, int IssuesWithoutPoints,
        List<GroupDto> Groups, bool Truncated, OtherDto? Other);
    private sealed record PageDto(int TotalCount);
    private sealed record ProblemDto(string Title);

    private static readonly DateTime At = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Groups_by_status_with_counts_points_and_empty()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "a", ctx.ToDoStatusId, 3, At);
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "b", ctx.ToDoStatusId, null, At);
        await MetricsTestData.SeedIssueAsync(ctx, "3", "ACME-3", "c", ctx.DoneStatusId, 5, At);
        await MetricsTestData.SeedIssueAsync(ctx, "4", "ACME-4", "d", null, 2, At);

        var chart = await ChartAsync(client, ctx.ProjectId, new { groupBy = "status" });

        // Issues descending, ties by key ascending, (Empty) last among equals.
        Assert.Equal(
            new[] { new GroupDto("To Do", 2, 3), new GroupDto("Done", 1, 5), new GroupDto(null, 1, 2) },
            chart.Groups);
        Assert.Equal(4, chart.TotalIssues);
        Assert.Equal(10, chart.TotalStoryPoints);
        Assert.Equal(1, chart.IssuesWithoutPoints);
        Assert.False(chart.Truncated);
        Assert.Null(chart.Other);
    }

    [Fact]
    public async Task Same_named_teams_are_one_group()
    {
        // Two boards can each have a team called "mobile": a reader sees one name, so one bar.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var t1 = await MetricsTestData.SeedTeamAsync(ctx, "mobile");
        var t2 = await MetricsTestData.SeedTeamAsync(ctx, "mobile");
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "a", null, 1, At, t1);
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "b", null, 2, At, t2);

        var chart = await ChartAsync(client, ctx.ProjectId, new { groupBy = "team" });

        Assert.Equal(new[] { new GroupDto("mobile", 2, 3) }, chart.Groups);
    }

    [Fact]
    public async Task Status_category_blank_is_empty()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var blank = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'9','Odd','  ')",
                ("i", blank), ("t", tenant), ("c", ctx.ConnectionId));
        }
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "a", ctx.DoneStatusId, null, At);
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "b", blank, null, At);
        await MetricsTestData.SeedIssueAsync(ctx, "3", "ACME-3", "c", null, null, At);

        var chart = await ChartAsync(client, ctx.ProjectId, new { groupBy = "statusCategory" });

        Assert.Equal(new[] { new GroupDto(null, 2, 0), new GroupDto("done", 1, 0) }, chart.Groups);
    }

    [Fact]
    public async Task Months_bucket_in_utc()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "a", null, null,
            new DateTime(2026, 10, 1, 0, 30, 0, DateTimeKind.Utc));
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "b", null, null,
            new DateTime(2026, 9, 30, 23, 30, 0, DateTimeKind.Utc));
        await MetricsTestData.SeedIssueAsync(ctx, "3", "ACME-3", "c", null, null,
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var created = await ChartAsync(client, ctx.ProjectId, new { groupBy = "createdAt" });
        var updated = await ChartAsync(client, ctx.ProjectId, new { groupBy = "updatedAt" });

        var expected = new[] { new GroupDto("2026-09", 2, 0), new GroupDto("2026-10", 1, 0) };
        Assert.Equal(expected, created.Groups);
        Assert.Equal(expected, updated.Groups);   // SeedIssueAsync sets updated = created
    }

    [Fact]
    public async Task Story_points_group_by_value_ascending_with_empty_last()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "a", null, 8, At);
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "b", null, 0.5m, At);
        await MetricsTestData.SeedIssueAsync(ctx, "3", "ACME-3", "c", null, null, At);
        await MetricsTestData.SeedIssueAsync(ctx, "4", "ACME-4", "d", null, 8, At);

        var chart = await ChartAsync(client, ctx.ProjectId, new { groupBy = "storyPoints" });

        Assert.Equal(
            new[] { new GroupDto("0.5", 1, 0.5m), new GroupDto("8", 2, 16), new GroupDto(null, 1, 0) },
            chart.Groups);
    }

    [Fact]
    public async Task Total_matches_the_query_count_for_the_same_filter()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "C-0", "C-0", "a", ctx.ToDoStatusId, 1, At);
        await MetricsTestData.SeedIssueAsync(ctx, "C-1", "C-1", "b", ctx.DoneStatusId, 2, At);
        await MetricsTestData.SeedIssueAsync(ctx, "C-2", "C-2", "c", ctx.InProgressStatusId, 3, At);
        await MetricsTestData.SeedIssueAsync(ctx, "C-3", "C-3", "d", null, 4, At);
        await HistoryTestData.SeedChangeAsync(ctx, "C-1", "Sprint", null, "S1");
        await HistoryTestData.SeedChangeAsync(ctx, "C-2", "Sprint", null, "S1");

        // Done OR (changed sprint at least once AND not To Do).
        var filter = new
        {
            logic = "Or",
            conditions = new object[] { new { field = "status", op = "Eq", value = "Done" } },
            groups = new object[]
            {
                new
                {
                    logic = "And",
                    conditions = new object[]
                    {
                        new { field = "history", op = "ChangeCount", value = new { historyField = "Sprint", compare = "Gte", count = 1 } },
                        new { field = "status", op = "Neq", value = "To Do" },
                    },
                },
            },
        };

        var chart = await ChartAsync(client, ctx.ProjectId, new { filter, groupBy = "status" });
        var query = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/issues/query", new { filter, page = 1, pageSize = 50 });
        var page = (await query.Content.ReadFromJsonAsync<PageDto>())!;

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(page.TotalCount, chart.TotalIssues);
        Assert.Equal(5, chart.TotalStoryPoints);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Bad_groupBy_is_400_listing_allowed_fields(string? groupBy)
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/chart", new { groupBy });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var title = (await resp.Content.ReadFromJsonAsync<ProblemDto>())!.Title;
        Assert.Contains($"'{groupBy}'", title);
        Assert.Contains("statusCategory", title);
    }

    [Fact]
    public async Task Invalid_filter_is_the_same_400_as_the_query()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var filter = new { logic = "And", conditions = new object[] { new { field = "nonsense", op = "Eq", value = "x" } } };

        var chart = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/chart", new { filter, groupBy = "status" });
        var query = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", new { filter });

        Assert.Equal(HttpStatusCode.BadRequest, chart.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(
            (await query.Content.ReadFromJsonAsync<ProblemDto>())!.Title,
            (await chart.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    [Fact]
    public async Task Unknown_project_is_404()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/issues/chart", new { groupBy = "nonsense" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_project_is_404()
    {
        var (clientA, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, tenantB) = await MetricsTestData.AuthedTenantAsync(_factory);
        var b = await MetricsTestData.SeedProjectAsync(tenantB);
        await MetricsTestData.SeedIssueAsync(b, "1", "ACME-1", "a", b.ToDoStatusId, 1, At);

        var resp = await clientA.PostAsJsonAsync($"/api/projects/{b.ProjectId}/issues/chart", new { groupBy = "status" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task<ChartDto> ChartAsync(HttpClient client, Guid projectId, object request)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/chart", request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ChartDto>())!;
    }
}
